// jern-symbols: exact definitions and references for the jern runtime,
// parsed with tree-sitter. One static binary, no network, no state. The
// runtime hands it files and a queries directory; it answers with one JSON
// object per line and nothing else on stdout.
//
//   jern-symbols outline <file>                  definitions in one file
//   jern-symbols defs [--name N|--iname N]       definitions in the files on stdin
//   jern-symbols refs <name>                     references in the files on stdin
//   jern-symbols languages                       the extensions understood
//
// --queries <dir> (or JERN_SYMBOLS_QUERIES) names the directory holding the
// grammars' tags queries, <grammar>.scm, shipped beside the binary.

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <ctype.h>
#include <tree_sitter/api.h>

const TSLanguage *tree_sitter_python(void);
const TSLanguage *tree_sitter_javascript(void);
const TSLanguage *tree_sitter_typescript(void);
const TSLanguage *tree_sitter_tsx(void);
const TSLanguage *tree_sitter_c_sharp(void);
const TSLanguage *tree_sitter_java(void);
const TSLanguage *tree_sitter_go(void);
const TSLanguage *tree_sitter_rust(void);

typedef struct {
  const char *extension;
  const char *grammar;   // the queries file, <grammar>.scm
  const TSLanguage *(*language)(void);
} Language;

static const Language languages[] = {
  { ".py", "python", tree_sitter_python },
  { ".pyi", "python", tree_sitter_python },
  { ".js", "javascript", tree_sitter_javascript },
  { ".jsx", "javascript", tree_sitter_javascript },
  { ".mjs", "javascript", tree_sitter_javascript },
  { ".cjs", "javascript", tree_sitter_javascript },
  { ".ts", "typescript", tree_sitter_typescript },
  { ".mts", "typescript", tree_sitter_typescript },
  { ".cts", "typescript", tree_sitter_typescript },
  { ".tsx", "typescript", tree_sitter_tsx },
  { ".cs", "c-sharp", tree_sitter_c_sharp },
  { ".java", "java", tree_sitter_java },
  { ".go", "go", tree_sitter_go },
  { ".rs", "rust", tree_sitter_rust },
};

static const size_t MAX_FILE_BYTES = 8 * 1024 * 1024;

static const Language *language_for(const char *path) {
  const char *dot = strrchr(path, '.');
  if (!dot) return NULL;
  size_t n = strlen(dot);
  char ext[16];
  if (n >= sizeof ext) return NULL;
  for (size_t i = 0; i <= n; i++) ext[i] = (char)tolower((unsigned char)dot[i]);
  for (size_t i = 0; i < sizeof languages / sizeof languages[0]; i++)
    if (strcmp(languages[i].extension, ext) == 0) return &languages[i];
  return NULL;
}

static char *read_file(const char *path, size_t *length) {
  FILE *f = fopen(path, "rb");
  if (!f) return NULL;
  fseek(f, 0, SEEK_END);
  long size = ftell(f);
  fseek(f, 0, SEEK_SET);
  if (size < 0 || (size_t)size > MAX_FILE_BYTES) { fclose(f); return NULL; }
  char *buffer = malloc((size_t)size + 1);
  if (!buffer) { fclose(f); return NULL; }
  size_t read = fread(buffer, 1, (size_t)size, f);
  fclose(f);
  buffer[read] = '\0';
  *length = read;
  return buffer;
}

static void json_string(const char *s, size_t length) {
  putchar('"');
  for (size_t i = 0; i < length; i++) {
    unsigned char c = (unsigned char)s[i];
    switch (c) {
      case '"': fputs("\\\"", stdout); break;
      case '\\': fputs("\\\\", stdout); break;
      case '\n': fputs("\\n", stdout); break;
      case '\r': fputs("\\r", stdout); break;
      case '\t': fputs("\\t", stdout); break;
      default:
        if (c < 0x20) printf("\\u%04x", c); else putchar(c);
    }
  }
  putchar('"');
}

// The queries directory, resolved once.
static const char *queries_dir = NULL;

typedef struct {
  const Language *language;
  TSQuery *query;
} LoadedQuery;

static LoadedQuery loaded[sizeof languages / sizeof languages[0]];
static size_t loaded_count = 0;

static TSQuery *query_for(const Language *language) {
  for (size_t i = 0; i < loaded_count; i++)
    if (strcmp(loaded[i].language->grammar, language->grammar) == 0
        && loaded[i].language->language == language->language)
      return loaded[i].query;
  char path[4096];
  snprintf(path, sizeof path, "%s/%s.scm", queries_dir ? queries_dir : "queries", language->grammar);
  size_t length = 0;
  char *source = read_file(path, &length);
  if (!source) {
    fprintf(stderr, "jern-symbols: cannot read query %s\n", path);
    exit(2);
  }
  uint32_t error_offset = 0;
  TSQueryError error_type = TSQueryErrorNone;
  TSQuery *query = ts_query_new(language->language(), source, (uint32_t)length, &error_offset, &error_type);
  free(source);
  if (!query) {
    fprintf(stderr, "jern-symbols: query %s failed at offset %u (error %d)\n", path, error_offset, (int)error_type);
    exit(2);
  }
  loaded[loaded_count].language = language;
  loaded[loaded_count].query = query;
  loaded_count++;
  return query;
}

typedef struct {
  char kind[32];
  uint32_t name_start, name_end;   // bytes of the @name node
  uint32_t start, end;             // bytes of the definition node
  uint32_t start_row, end_row;
} Definition;

typedef struct {
  Definition *items;
  size_t count, capacity;
} Definitions;

static void definitions_add(Definitions *defs, Definition d) {
  for (size_t i = 0; i < defs->count; i++)
    // The same node matched by two patterns (rust captures a method as a
    // function too) is one definition; the first pattern names its kind.
    if (defs->items[i].name_start == d.name_start && defs->items[i].start == d.start)
      return;
  if (defs->count == defs->capacity) {
    defs->capacity = defs->capacity ? defs->capacity * 2 : 64;
    defs->items = realloc(defs->items, defs->capacity * sizeof(Definition));
  }
  defs->items[defs->count++] = d;
}

static int definition_order(const void *a, const void *b) {
  const Definition *x = a, *y = b;
  if (x->start != y->start) return x->start < y->start ? -1 : 1;
  return x->name_start < y->name_start ? -1 : (x->name_start > y->name_start ? 1 : 0);
}

typedef struct {
  char kind[32];
  uint32_t name_start;
} ReferenceKind;

typedef struct {
  ReferenceKind *items;
  size_t count, capacity;
} ReferenceKinds;

static void reference_kinds_add(ReferenceKinds *refs, ReferenceKind r) {
  if (refs->count == refs->capacity) {
    refs->capacity = refs->capacity ? refs->capacity * 2 : 64;
    refs->items = realloc(refs->items, refs->capacity * sizeof(ReferenceKind));
  }
  refs->items[refs->count++] = r;
}

// Runs the tags query over the tree: every @definition.* with its @name,
// and every @reference.* with its @name.
static void collect(const TSQuery *query, TSNode root, Definitions *defs, ReferenceKinds *refs) {
  TSQueryCursor *cursor = ts_query_cursor_new();
  ts_query_cursor_exec(cursor, query, root);
  TSQueryMatch match;
  while (ts_query_cursor_next_match(cursor, &match)) {
    TSNode name_node = {0};
    int has_name = 0;
    TSNode def_node = {0};
    const char *def_kind = NULL;
    const char *ref_kind = NULL;
    for (uint16_t i = 0; i < match.capture_count; i++) {
      uint32_t length = 0;
      const char *capture = ts_query_capture_name_for_id(query, match.captures[i].index, &length);
      if (length == 4 && strncmp(capture, "name", 4) == 0) {
        name_node = match.captures[i].node;
        has_name = 1;
      } else if (length > 11 && strncmp(capture, "definition.", 11) == 0) {
        def_node = match.captures[i].node;
        def_kind = capture + 11;
      } else if (length > 10 && strncmp(capture, "reference.", 10) == 0) {
        ref_kind = capture + 10;
      }
    }
    if (!has_name) continue;
    if (def_kind) {
      Definition d;
      memset(&d, 0, sizeof d);
      strncpy(d.kind, def_kind, sizeof d.kind - 1);
      // The capture name runs to the end of its buffer; cut at the first
      // non-identifier character.
      for (size_t k = 0; k < sizeof d.kind; k++)
        if (!d.kind[k] || !(isalnum((unsigned char)d.kind[k]) || d.kind[k] == '_')) { d.kind[k] = '\0'; break; }
      d.name_start = ts_node_start_byte(name_node);
      d.name_end = ts_node_end_byte(name_node);
      d.start = ts_node_start_byte(def_node);
      d.end = ts_node_end_byte(def_node);
      d.start_row = ts_node_start_point(def_node).row;
      d.end_row = ts_node_end_point(def_node).row;
      definitions_add(defs, d);
    } else if (ref_kind && refs) {
      ReferenceKind r;
      memset(&r, 0, sizeof r);
      strncpy(r.kind, ref_kind, sizeof r.kind - 1);
      for (size_t k = 0; k < sizeof r.kind; k++)
        if (!r.kind[k] || !(isalnum((unsigned char)r.kind[k]) || r.kind[k] == '_')) { r.kind[k] = '\0'; break; }
      r.name_start = ts_node_start_byte(name_node);
      reference_kinds_add(refs, r);
    }
  }
  ts_query_cursor_delete(cursor);
}

static void print_line(const char *source, size_t length, uint32_t row_start_byte, const char *label) {
  // The line holding byte row_start_byte, trimmed, at most 200 bytes.
  size_t begin = row_start_byte;
  while (begin > 0 && source[begin - 1] != '\n') begin--;
  size_t end = row_start_byte;
  while (end < length && source[end] != '\n' && source[end] != '\r') end++;
  while (begin < end && isspace((unsigned char)source[begin])) begin++;
  while (end > begin && isspace((unsigned char)source[end - 1])) end--;
  size_t shown = end - begin;
  if (shown > 200) shown = 200;
  printf(",\"%s\":", label);
  json_string(source + begin, shown);
}

static void print_definition(const char *file, const char *source, size_t length, const Definition *d) {
  printf("{\"kind\":");
  json_string(d->kind, strlen(d->kind));
  printf(",\"name\":");
  json_string(source + d->name_start, d->name_end - d->name_start);
  printf(",\"start\":%u,\"end\":%u", d->start_row + 1, d->end_row + 1);
  if (file) { printf(",\"file\":"); json_string(file, strlen(file)); }
  print_line(source, length, d->start, "signature");
  printf("}\n");
}

static int name_matches(const char *source, const Definition *d, const char *name, int ignore_case) {
  size_t n = d->name_end - d->name_start;
  if (n != strlen(name)) return 0;
  for (size_t i = 0; i < n; i++) {
    unsigned char a = (unsigned char)source[d->name_start + i], b = (unsigned char)name[i];
    if (ignore_case ? tolower(a) != tolower(b) : a != b) return 0;
  }
  return 1;
}

static TSTree *parse(const Language *language, const char *source, size_t length) {
  TSParser *parser = ts_parser_new();
  ts_parser_set_language(parser, language->language());
  TSTree *tree = ts_parser_parse_string(parser, NULL, source, (uint32_t)length);
  ts_parser_delete(parser);
  return tree;
}

static int outline(const char *file) {
  const Language *language = language_for(file);
  if (!language) { fprintf(stderr, "jern-symbols: no grammar for %s\n", file); return 3; }
  size_t length = 0;
  char *source = read_file(file, &length);
  if (!source) { fprintf(stderr, "jern-symbols: cannot read %s\n", file); return 3; }
  TSTree *tree = parse(language, source, length);
  Definitions defs = {0};
  collect(query_for(language), ts_tree_root_node(tree), &defs, NULL);
  qsort(defs.items, defs.count, sizeof(Definition), definition_order);
  for (size_t i = 0; i < defs.count; i++) print_definition(NULL, source, length, &defs.items[i]);
  free(defs.items);
  ts_tree_delete(tree);
  free(source);
  return 0;
}

static int next_path(char *buffer, size_t size) {
  if (!fgets(buffer, (int)size, stdin)) return 0;
  size_t n = strlen(buffer);
  while (n > 0 && (buffer[n - 1] == '\n' || buffer[n - 1] == '\r')) buffer[--n] = '\0';
  return 1;
}

static int defs_command(const char *name, int ignore_case) {
  char path[4096];
  while (next_path(path, sizeof path)) {
    if (!path[0]) continue;
    const Language *language = language_for(path);
    if (!language) continue;
    size_t length = 0;
    char *source = read_file(path, &length);
    if (!source) continue;
    TSTree *tree = parse(language, source, length);
    Definitions defs = {0};
    collect(query_for(language), ts_tree_root_node(tree), &defs, NULL);
    qsort(defs.items, defs.count, sizeof(Definition), definition_order);
    for (size_t i = 0; i < defs.count; i++)
      if (!name || name_matches(source, &defs.items[i], name, ignore_case))
        print_definition(path, source, length, &defs.items[i]);
    free(defs.items);
    ts_tree_delete(tree);
    free(source);
  }
  return 0;
}

static int inside_string_or_comment(TSNode node) {
  TSNode current = ts_node_parent(node);
  while (!ts_node_is_null(current)) {
    const char *type = ts_node_type(current);
    if (strstr(type, "string") || strstr(type, "comment") || strstr(type, "template")) return 1;
    current = ts_node_parent(current);
  }
  return 0;
}

static const Definition *enclosing(const Definitions *defs, uint32_t position, const Definition *skip) {
  const Definition *best = NULL;
  for (size_t i = 0; i < defs->count; i++) {
    const Definition *d = &defs->items[i];
    if (d == skip) continue;
    if (d->start <= position && position < d->end)
      if (!best || (d->end - d->start) < (best->end - best->start)) best = d;
  }
  return best;
}

static int refs_command(const char *name) {
  size_t name_length = strlen(name);
  char path[4096];
  while (next_path(path, sizeof path)) {
    if (!path[0]) continue;
    const Language *language = language_for(path);
    if (!language) continue;
    size_t length = 0;
    char *source = read_file(path, &length);
    if (!source) continue;
    TSTree *tree = parse(language, source, length);
    TSNode root = ts_tree_root_node(tree);
    Definitions defs = {0};
    ReferenceKinds refs = {0};
    collect(query_for(language), root, &defs, &refs);
    // Walk every node; leaves whose text is the name are references,
    // unless they sit inside a string or comment.
    TSTreeCursor cursor = ts_tree_cursor_new(root);
    int descending = 1;
    for (;;) {
      TSNode node = ts_tree_cursor_current_node(&cursor);
      if (descending && ts_node_child_count(node) > 0 && ts_tree_cursor_goto_first_child(&cursor)) continue;
      if (ts_node_child_count(node) == 0 && ts_node_is_named(node)) {
        uint32_t start = ts_node_start_byte(node), end = ts_node_end_byte(node);
        if (end - start == name_length && strncmp(source + start, name, name_length) == 0 && !inside_string_or_comment(node)) {
          const Definition *own = NULL;
          for (size_t i = 0; i < defs.count; i++)
            if (defs.items[i].name_start == start) { own = &defs.items[i]; break; }
          const char *kind = own ? "definition" : "identifier";
          if (!own)
            for (size_t i = 0; i < refs.count; i++)
              if (refs.items[i].name_start == start) { kind = refs.items[i].kind; break; }
          const Definition *scope = enclosing(&defs, start, own);
          TSPoint point = ts_node_start_point(node);
          printf("{\"file\":");
          json_string(path, strlen(path));
          printf(",\"line\":%u,\"column\":%u,\"kind\":", point.row + 1, point.column + 1);
          json_string(kind, strlen(kind));
          printf(",\"scope\":");
          if (scope) json_string(source + scope->name_start, scope->name_end - scope->name_start);
          else json_string("", 0);
          print_line(source, length, start, "text");
          printf("}\n");
        }
      }
      if (ts_tree_cursor_goto_next_sibling(&cursor)) { descending = 1; continue; }
      // Climb until a parent has a next sibling.
      int climbed = 0;
      while (ts_tree_cursor_goto_parent(&cursor)) {
        if (ts_tree_cursor_goto_next_sibling(&cursor)) { climbed = 1; break; }
      }
      if (!climbed) break;
      descending = 1;
    }
    ts_tree_cursor_delete(&cursor);
    free(defs.items);
    free(refs.items);
    ts_tree_delete(tree);
    free(source);
  }
  return 0;
}

int main(int argc, char **argv) {
  queries_dir = getenv("JERN_SYMBOLS_QUERIES");
  int arg = 1;
  while (arg + 1 < argc && strcmp(argv[arg], "--queries") == 0) { queries_dir = argv[arg + 1]; arg += 2; }
  if (arg >= argc) { fputs("usage: jern-symbols [--queries <dir>] outline <file> | defs [--name N | --iname N] | refs <name> | languages\n", stderr); return 1; }
  const char *command = argv[arg];
  if (strcmp(command, "languages") == 0) {
    for (size_t i = 0; i < sizeof languages / sizeof languages[0]; i++)
      printf("%s %s\n", languages[i].extension, languages[i].grammar);
    return 0;
  }
  if (strcmp(command, "outline") == 0) {
    if (arg + 1 >= argc) { fputs("outline needs a file\n", stderr); return 1; }
    return outline(argv[arg + 1]);
  }
  if (strcmp(command, "defs") == 0) {
    const char *name = NULL;
    int ignore_case = 0;
    if (arg + 2 < argc && strcmp(argv[arg + 1], "--name") == 0) name = argv[arg + 2];
    else if (arg + 2 < argc && strcmp(argv[arg + 1], "--iname") == 0) { name = argv[arg + 2]; ignore_case = 1; }
    return defs_command(name, ignore_case);
  }
  if (strcmp(command, "refs") == 0) {
    if (arg + 1 >= argc) { fputs("refs needs a name\n", stderr); return 1; }
    return refs_command(argv[arg + 1]);
  }
  fprintf(stderr, "jern-symbols: unknown command %s\n", command);
  return 1;
}

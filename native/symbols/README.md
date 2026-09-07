# jern-symbols

The tree-sitter helper behind jern's `outline`, `read_symbol`, and
`references` tools. One static C program: the tree-sitter core, seven
grammars (Python, JavaScript, TypeScript and TSX, C#, Java, Go, Rust), and
`main.c`, which reads file paths and prints one JSON object per definition
or reference. The grammars' own `tags.scm` queries, checked in under
`queries/`, say what a definition and a reference are in each language.

    bash fetch.sh   # downloads the pinned sources into vendor/ (sha256-checked)
    bash build.sh   # builds out/jern-symbols; JERN_SYMBOLS_ARCH=x86_64 cross-compiles on an arm64 Mac

The .NET build copies `out/jern-symbols` and `queries/*.scm` beside the
binary when they exist; without them the tools fall back to line patterns.
`JERN_SYMBOLS` and `JERN_SYMBOLS_QUERIES` point the runtime elsewhere.

    jern-symbols outline <file>                 definitions in one file
    jern-symbols defs [--name N | --iname N]    definitions in the files listed on stdin
    jern-symbols refs <name>                    references in the files listed on stdin
    jern-symbols languages                      the extensions understood

Sources and their digests are pinned in `sources.txt`; bump them
deliberately and run `fetch.sh` again to refresh the queries.

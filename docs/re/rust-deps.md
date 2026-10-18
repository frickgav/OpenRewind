## evidence_id

ev_18bb0b1a4272df6033bbde45dbf045d586efd122964d3496525ccef09402aea3

## subject.name

screenpipe-app

## subject.digest

| Key    | Value                                                            |
|--------|------------------------------------------------------------------|
| sha256 | 5b2d1b17f4f28ef80c62ac344020c19434ab6a063d09de0ec45c40a37c3dae9e |

## subject.local_path

/Applications/screenpipe.app/Contents/MacOS/screenpipe-app

## subject.format

mach-o

## subject.architecture

arm64

## provider

| Key     | Value                      |
|---------|----------------------------|
| id      | rust-auditable             |
| name    | REA Rust dependency reader |
| version | 1                          |

## analysis_profile

null

## predicate_type

rea.analysis

## operation

inspect_rust_binary

## parameters

| Key  | Value                                                      |
|------|------------------------------------------------------------|
| path | /Applications/screenpipe.app/Contents/MacOS/screenpipe-app |

## raw_result

null

## normalized_result.artifact

| Key    | Value                                                            |
|--------|------------------------------------------------------------------|
| path   | /Applications/screenpipe.app/Contents/MacOS/screenpipe-app       |
| sha256 | 5b2d1b17f4f28ef80c62ac344020c19434ab6a063d09de0ec45c40a37c3dae9e |
| bytes  | 221698064                                                        |

## normalized_result.format

macho

## normalized_result.architecture

arm64

## normalized_result.bits

64

## normalized_result.byte_order

little

## normalized_result.auditable_metadata

null

## normalized_result.rustc_commit.evidence_kind

inferred

## normalized_result.rustc_commit.hashes

| hash                                     | file_offset |
|------------------------------------------|-------------|
| 4a4ef493e3a1488c6e321570238084b38948f6db | 152299452   |

## normalized_result.limitations

Embedded dependency metadata is file content. It does not authenticate package names, versions, sources, or the compiler that produced the binary.,Only the .dep-v0 dependency section and read-only compiler path strings are inspected. Functions, types, and runtime behavior are not recovered.,Resource guards bound the selected file to 256 MiB, a dependency section to 1 MiB, inflated JSON to the smaller of 8 MiB and 64 times the section size, and the complete report to 8 MiB. Package and dependency counts are admitted against that output budget before schema cloning, graph objects, or reverse-edge allocation.,No .dep-v0 section was found. Absence of .dep-v0 does not mean the binary has no dependencies.,The rustc commit hash is inferred from a /rustc/<40-hex>/ path in read-only data. It does not by itself prove which compiler produced the binary.

## confidence

observed

## authority

shipped-artifact

## environment

null

## limitations

Embedded dependency metadata is file content. It does not authenticate package names, versions, sources, or the compiler that produced the binary.,Only the .dep-v0 dependency section and read-only compiler path strings are inspected. Functions, types, and runtime behavior are not recovered.,Resource guards bound the selected file to 256 MiB, a dependency section to 1 MiB, inflated JSON to the smaller of 8 MiB and 64 times the section size, and the complete report to 8 MiB. Package and dependency counts are admitted against that output budget before schema cloning, graph objects, or reverse-edge allocation.,No .dep-v0 section was found. Absence of .dep-v0 does not mean the binary has no dependencies.,The rustc commit hash is inferred from a /rustc/<40-hex>/ path in read-only data. It does not by itself prove which compiler produced the binary.

## relationship_coverage

null

## locations

| kind          | path                                                       |
|---------------|------------------------------------------------------------|
| artifact-path | /Applications/screenpipe.app/Contents/MacOS/screenpipe-app |

## evidence_links


# Format Converter

[![.NET Tool](https://img.shields.io/badge/.NET-Tool-blue)](https://www.nuget.org/packages/formatconverter)

*"I woke up and thought: let's cook something..."*  
So here it is - a **Format Converter** that magically transforms your files between different formats, like a culinary master turning ingredients into a gourmet dish.

---

## Supported Formats

This tool converts between the following data formats:

| Format       | Extension   |
|--------------|-------------|
| **JSON**     | `.json`     |
| **XML**      | `.xml`      |
| **YAML**     | `.yaml`     |
| **MessagePack** | `.msgpack` |
| **CBOR**     | `.cbor`     |
| **Protobuf**   | `.pb`  |
| **BXML**   | `.bxml`  |
| **TOML**   | `.toml`   |

---

## How to Use

Basic Command
```
formatconverter -i <input-file> --input-format <format> --output-format <format> [options]
```
- `-i <input-file>` → path to your file (use - for stdin).
- `--input-format` → one of: json, xml, yaml, messagepack, cbor, protobuf, bxml.
- `--output-format` → target format.

If you omit `-o <output-file>`, the tool auto-generates one with the correct extension.

### Available Options
#### General

- `--o, --output` → specify output file path
- `--f, --force` → overwrite existing files
- `--v, --verbosity <None/Error/Warning/Info/Debug/Trace>` → enable detailed logs
- `--version` → show tool version
- `--l, --list-formats` → list all supported formats
- `--encoding <enc>` → set input/output encoding (default: utf-8)
#### Formatting

- `--indent <size>` → set indentation (0 = tabs)
- `--minify` → compact output
- `--pretty` → pretty print output (default: true)
- `--sort-keys` → sort object keys alphabetically
#### JSON Specific

- `--json-escape-unicode` → escape non-ASCII characters
- `--json-allow-trailing-commas` → allow trailing commas
- `--json-strict-property-names` → require quoted property names (default: true)
- `--json-allow-single-quotes` → allow single quotes
#### XML Specific

- `--xml-root <name>` → custom root element name
- `--xml-namespace <uri>` → namespace URI
- `--xml-namespace-prefix <prefix>` → namespace prefix
- `--xml-declaration` / `--xml-standalone` → include XML declaration / add standalone="yes"
- `--xml-cdata` → wrap text in CDATA
- `--xml-attributes` → convert properties to attributes
#### YAML Specific

- `--yaml-flow-style` → use flow (inline) style
- `--yaml-explicit-start` / `--yaml-explicit-end` → add --- / ... markers
- `--yaml-quote-strings` → quote all strings
- `--yaml-canonical` → use canonical YAML
- `--yaml-allow-duplicate-keys` → allow duplicate keys
#### TOML Specific

- `toml-array-of-tables` → use array-of-tables ([[table]]) (default: true)
- `toml-multiline-strings` → use multiline strings
- `toml-strict-types` → Enforce strict type conversion
- `--toml-array-wrapper-key <key>` → wrap root arrays under a key (default: item)

#### MessagePack
- `--msgpack-contractless` → use contractless resolver (default: true)
- `--msgpack-old-spec` → use legacy format

#### CBOR
- `--cbor-indefinite-length` → allow indefinite arrays/maps
- `--cbor-multiple-content` → allow multiple top-level items
- `--cbor-canonical` → use canonical encoding
- `--cbor-tags` → preserve semantic tags
- `--cbor-datetime-tag` → encode dates with CBOR datetime tags
- `--cbor-bignum` → use big number tags for large integers

#### BXML
- `--bxml-endian <littleendian/bigendian>`
- `--bxml-compressArrays` enable array compression (default: true)

#### Other

- `--compress <gzip|deflate|brotli|lz4>` → compress output (Base64 encoded)
- `--strict` → fail on warnings
- `--ignore-errors` → keep going even on non-critical errors
- `--streaming` → stream large files
- `--number-format <decimal|hexadecimal|scientific|raw|binary>`
- `--date-format <format>` (e.g. yyyy-MM-dd, ISO8601)
- `--timezone <tz>` (e.g. UTC, America/New_York)
- `--array-wrap` → wrap single items in arrays
- `--flatten-arrays` → flatten nested arrays
- `--max-depth <n>` → maximum nesting depth
---
## Examples

Convert JSON to XML:
```
formatconverter -i data.json --input-format json --output-format xml
```

Convert YAML to JSON with verbose output:
```
formatconverter -i config.yaml --input-format yaml --output-format json --verbose
```

Force overwrite existing files:
```
formatconverter -i input.xml --input-format xml --output-format json --force
```

Pretty-print JSON with sorted keys:
```
formatconverter -i data.json --input-format json --output-format json --indent 4 --sort-keys
```

Compress output:
```
formatconverter -i big.json --input-format json --output-format yaml --compress gzip
```
---

## Important Notes
- `--force` will overwrite files **without mercy**. Use wisely.
- Not all formats **preserve original data types** with full accuracy.
- Stdin/stdout supported via - as filename.
- If the tool crashes, **blame the universe** 🌌 (*or... read the error message*).
---

## Installation
```
dotnet tool install --global formatconverter
```

### Update:
```
dotnet tool update --global formatconverter
```
---
Bug reports, and conversion format suggestions are welcome!
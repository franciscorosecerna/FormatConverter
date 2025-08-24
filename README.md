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

---

## How to Use

Basic Command
```
formatconverter -i <input-file> --input-format <format> --output-format <format> [options]
```
- -i <input-file> → path to your file (use - for stdin).
- --input-format → one of: json, xml, yaml, messagepack, cbor, protobuf, bxml.
- --output-format → target format.

If you omit -o <output-file>, the tool auto-generates one with the correct extension.

### Available Options
#### General

- --o, --output → specify output file path
- --f, --force → overwrite existing files
- --v, --verbose → enable detailed logs
- --version → show tool version
- --l, --list-formats → list all supported formats
- --encoding <enc> → set input/output encoding (default: utf-8)
#### Formatting

- --indent <size> → set indentation (0 = tabs)
- --minify → compact output
- --pretty → pretty print output (default: true)
- --sort-keys → sort object keys alphabetically
#### JSON Specific

- --json-escape-unicode
- --json-trailing-commas
- --json-quote-names (default: true)
- --json-single-quotes
#### XML Specific

- --xml-root <name>
- --xml-namespace <uri>
- --xml-namespace-prefix <prefix>
- --xml-declaration / --xml-standalone
- --xml-cdata
- --xml-attributes
#### YAML Specific

- --yaml-flow-style
- --yaml-explicit-start / --yaml-explicit-end
- --yaml-quote-strings
- --yaml-canonical
#### Other

- --compress <gzip|deflate|brotli> → compress output (Base64 encoded)
- --compression-level <1-9> (default: 6)
- --validate <schema> → validate against schema file
- --strict → fail on warnings
- --ignore-errors → keep going even on non-critical errors
- --streaming → stream large files
- --buffer-size <bytes> (default: 4096)
- --number-format <decimal|hexadecimal|scientific>
- --date-format <format> (e.g. yyyy-MM-dd, ISO8601)
- --timezone <tz> (e.g. UTC, America/New_York)
- --array-wrap → wrap single items in arrays
- --flatten-arrays → flatten nested arrays
- --max-depth <n> → maximum nesting depth
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
- --force will overwrite files without mercy. Use wisely.
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

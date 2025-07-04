# Format Converter

*"I woke up and thought: let's cook something..."*  
So here it is — a **Format Converter** that magically transforms your files between different formats, like a culinary master turning ingredients into a gourmet dish.

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
---

## How to Use

### Basic Command

```
dotnet run -- <input-file> <input-format> <output-format> [--force]
```
- Replace `<input-file>` with your file path.
- Replace `<input-format>` and `<output-format>` with one of: json, xml, yaml, messagepack, cbor.
- Use `--force` to overwrite existing files.
---

## Important Notes
- Use `--force` carefully — it will overwrite files without asking.
- If the tool crashes, blame the universe 🌌 (or read the error message, as you prefer).
---

## Installation
- Clone this repo:
```
git clone https://github.com/yourusername/FormatConverter.git
cd FormatConverter
```
- Build and run:
```
dotnet build
dotnet run
```

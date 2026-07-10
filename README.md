# Claricite

**Claricite** is a command-line utility designed to verify the validity of bibliographic references in academic PDF files. It extracts references locally by default, or with Grobid when requested, and cross-references them against major academic databases to ensure they are verifiable.

## Prerequisites

* **Grobid Service**: Optional. Only needed when running with `-extractor grobid`; provide the endpoint with `-grobidUrl`.
* **.NET Runtime**: Ensure you have the appropriate .NET runtime installed (Target Framework: `net10.0`).

## Quick Download

Get the pre-compiled, self-contained executable for your operating system (Windows, macOS, or Ubuntu Linux):

👉 **[Download Latest Claricite Binaries Here](https://github.com/Noise477/Claricite/releases/latest)**

## Usage

Run the executable from your terminal.

### Basic Command Syntax

Windows:
```powershell
Claricite.exe [options] pdf_files_or_folders
```

Other platforms:
```bash
dotnet Claricite.dll [options] pdf_files_or_folders
```

## Examples

### 1. Process all PDFs in a folder using the default local extractor

```powershell
Claricite.exe -output failed_refs.html Papers/SIGCOMM25 
```

### 2. Process multiple PDF files and folders

```powershell
Claricite.exe -output failed_refs.html Papers/SIGCOMM25 somepaper.pdf anotherpaper.pdf
```

### 3. Process with Grobid explicitly

```powershell
Claricite.exe -extractor grobid -grobidUrl https://www.site.org/grobid -output failed_refs.html somepaper.pdf
```

## Command Line Options

| Option                | Description                                                                               |
| ----------------------| ----------------------------------------------------------------------------------------- |
| `-output`             | An HTML file where unverifiable references will be saved. Defaults to output.html.        |
| `-verbose`            | Prints detailed verification trace (similarity scores, API hits).                         |
| `-extractor`          | Reference extractor: `local` or `grobid`. Defaults to `local`.                            |
| `-maxConcurrency`     | Set the maximum number of concurrent API calls.                                           |
| `-grobidUrl`          | Set the URL of the GROBID server. Required only with `-extractor grobid`.                 |
| `-openAlexKey`        | Set the OpenAlex API key.                                                                 |
| `-semanticScholarKey` | Set the Semantic Scholar API key.                                                         |
| `-help`               | Display the help message.                                                                 |
| `-version`            | Display the current version of Claricite.                                                 |

Options may be specified in any of the following locations:

1. On the command line
2. In a local `Claricite.options` file in the current working directory
3. In a global `Claricite.options` file located alongside `Claricite.exe`

Precedence is applied in that order. Command-line options override local configuration values, and local configuration values override global configuration values.

For example, the following Claricite.options file configures custom API keys and a concurrency limit:

```txt
-openAlexKey OA1234567890OA1234567890OA1234567890
-semanticScholarKey SS1234567890SS1234567890SS1234567890
-maxConcurrency 12
```

## Output Format

Claricite records unverifiable references to an HTML file. 

If the output file already exists, Claricite appends new results to the end of the file rather than overwriting existing contents.

Documents with no unverifiable references are not written to the output file. 

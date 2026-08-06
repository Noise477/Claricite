# Claricite

**Claricite** is a command-line utility designed to verify the validity of bibliographic references in academic PDF files. It extracts references locally by default, or with Grobid when requested, and cross-references them against major academic databases to ensure they are verifiable.

The built-in local extractor requires no additional services. For higher extraction accuracy, using **GROBID is recommended**. GROBID uses machine-learning models to identify document structure and parse bibliographic references, making it generally more robust than rule-based local parsing across different PDF layouts and reference styles.

## Prerequisites

* **GROBID Service**: Optional, but recommended for higher extraction accuracy because it uses machine-learning models for document and reference parsing. Only needed when running with `-extractor grobid`; provide the endpoint with `-grobidUrl`.
* **.NET Runtime**: Ensure you have the appropriate .NET runtime installed (Target Framework: `net10.0`).

## Recommended GROBID Setup

The easiest way to run GROBID locally is with Docker. Install [Docker Desktop](https://docs.docker.com/get-started/get-docker/), then start the full GROBID image:

```bash
docker run -d --rm --name grobid --init --ulimit core=0 -p 8070:8070 grobid/grobid:0.9.0-full
```

The `full` image is recommended because it includes the machine-learning models used for more accurate reference parsing. Once started, GROBID is available at `http://localhost:8070`.

On an Apple Silicon Mac, add `--platform linux/amd64` if Docker reports a platform mismatch:

```bash
docker run -d --rm --name grobid --platform linux/amd64 --init --ulimit core=0 -p 8070:8070 grobid/grobid:0.9.0-full
```

To stop the service:

```bash
docker stop grobid
```

## Quick Download

Get the pre-compiled, self-contained executable for your operating system (Windows, macOS, or Ubuntu Linux):

👉 **[Download Latest Claricite Binaries Here](https://github.com/Noise477/Claricite/releases/latest)**
(Click and expand the **"Assets"** section at the bottom of the page to see the files).

## Usage

Run the executable from your terminal.

### Basic Command Syntax

Windows:
```powershell
Claricite.exe [options] pdf_files_or_folders
```

Other platforms:
```bash
Claricite [options] pdf_files_or_folders
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

### 3. Force IEEE-style local reference parsing

```powershell
Claricite.exe -style ieee -output failed_refs.html somepaper.pdf
```

When `-style ieee` is selected, the local extractor uses only IEEE bracket-numbered reference segmentation such as `[1]`, `[2]`, and `[3]`. If `-style` is omitted, Claricite keeps the existing automatic/default parser.

### 4. Force ACM Reference Format parsing

```powershell
Claricite.exe -style acm -output failed_refs.html somepaper.pdf
```

The ACM parser expects numbered references and extracts the standard ACM order separately:

```text
[1] Full Author Names. 2024. Article title. Publication information. https://doi.org/...
```

It supports normal ACM journal, conference, book, thesis, technical-report, web, and arXiv entries. The ACM parser is independent from the IEEE parser because ACM normally places the publication year between the author list and the title.

### 5. Process with Grobid

```powershell
Claricite.exe -extractor grobid -grobidUrl http://localhost:8070 -output failed_refs.html somepaper.pdf
```

## Command Line Options

| Option                | Description                                                                               |
| ----------------------| ----------------------------------------------------------------------------------------- |
| `-output`             | An HTML file where unverifiable references will be saved. Defaults to output.html.        |
| `-verbose`            | Prints detailed verification trace (similarity scores, API hits).                         |
| `-extractor`          | Reference extractor: `local` or `grobid`. Defaults to `local`.                            |
| `-style`              | Local reference style: `default`, `ieee`, or `acm`. Defaults to `default`.             |
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
-style ieee
```

For an ACM-formatted collection:

```txt
-style acm
```

## Output Format

Claricite records unverifiable references to an HTML file. 

If the output file already exists, Claricite appends new results to the end of the file rather than overwriting existing contents.

Documents with no unverifiable references are not written to the output file. 

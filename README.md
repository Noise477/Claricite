# CiteCheck

**CiteCheck** is a command-line utility designed to verify the validity of bibliographic references in academic PDF files. It extracts references using Grobid and cross-references them against major academic databases to ensure they are not fabricated or incorrect.

## Prerequisites

* **Grobid Service**: You need a running instance of Grobid to parse PDF files. For testing purposes, you may use our server. (redsox.uoa.auckland.ac.nz)
* **.NET Runtime**: Ensure you have the appropriate .NET runtime installed (Target Framework: `net10.0`).

## Configuration (`appsettings.json`)

Before running the tool, configure the `appsettings.json` file in the application directory. This file manages service URLs and API credentials.

```json
{
  "Grobid": {
    "BaseUrl": "https://redsox.uoa.auckland.ac.nz/grobid/"
  },
  "Apis": {
    "OpenAlexApiKey": "",
    "SemanticScholarApiKey": ""
  },
  "Processing": {
    "MaxConcurrency": 10,
    "Verbose": false
  }
}
```

### Key Settings

* `Grobid.BaseUrl`: The endpoint of your Grobid server.
* `Apis.OpenAlexApiKey`: Optional. Your API key for OpenAlex to improve rate limits.
* `Apis.SemanticScholarApiKey`: Optional. Your API key for Semantic Scholar.
* `Processing.MaxConcurrency`: The number of references to verify in parallel. Default is `10`.
* `Processing.Verbose`: Enables detailed processing logs when set to `true`.

## Usage

Run the executable from your terminal.

### Basic Command Syntax

```powershell
.\CiteCheck.exe -output <output_csv_path> <input_path>
```

## Examples

### 1. Process all PDFs in a folder

```powershell
.\CiteCheck.exe -output "C:\Reports\failed_refs.csv" "C:\Papers\SIGCOMM25"
```

### 2. Process a single PDF with detailed logs

```powershell
.\CiteCheck.exe -verbose -output "C:\Reports\check.csv" "C:\Papers\my_paper.pdf"
```

## Command Line Arguments

| Option     | Description                                                                         |
| ---------- | ----------------------------------------------------------------------------------- |
| `-output`  | Required. Path to the CSV file where failed (`NOT FOUND`) references will be saved. |
| `-verbose` | Optional. Prints detailed verification trace (similarity scores, API hits).         |
| `-help`    | Displays the help message.                                                          |
| `-version` | Displays the current version of the tool.                                           |

## Output Format

The tool generates a CSV file listing only the references that failed verification.

```csv
FileName,ReferenceIndex1,ReferenceIndex2,...
paper1.pdf,3,7,12
paper2.pdf,5,9
```

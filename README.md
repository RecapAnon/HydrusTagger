# HydrusTagger

`HydrusTagger` is a .NET application written in F# for automatically tagging media files in a [Hydrus](https://github.com/hydrusnetwork/hydrus) database using AI models including DeepDanbooru, Waifu Diffusion, and vision language models (VLMs) to generate descriptive, accurate tags for images and videos.

---

## Features

- **Hydrus API integration** to search and tag files.
- **DeepDanbooru** and **Waifu Diffusion** models for automatic general/anime-style tagging.
- **LLM-based captioning** via OpenAI-compatible APIs (e.g., OpenAI, local LLMs) to generate natural language descriptions converted into tags.
- **Video frame extraction** – automatically extracts the middle frame from videos for tagging.
- **Flexible configuration** via command-line arguments or app settings file.

---

## Usage

```bash
HydrusTagger [<tags>...] [options]
```

Searches for files in Hydrus matching the provided tags and applies AI-generated tags to them.

---

## Arguments

| Argument | Description |
|--------|-------------|
| `<tags>` | One or more tags (comma separated) to filter which files should be processed (e.g., `animated`, `safe`).

---

## Options

| Option | Description | Example |
|-------|-------------|--------|
| `--BaseUrl` | Base URL of the Hydrus client API (Defaults to `http://localhost:45869`). | `--BaseUrl http://localhost:5000` |
| `--HydrusClientAPIAccessKey` | API key for authenticating with the Hydrus client. | `--HydrusClientAPIAccessKey your-access-key` |
| `--DDModelPath` | Path to the DeepDanbooru model `.zip` file. | `--DDModelPath ./models/deepdanbooru.onnx` |
| `--DDLabelPath` | Path to the DeepDanbooru labels file. | `--DDLabelPath ./models/tags.txt` |
| `--WDModelPath` | Path to the Waifu Diffusion model `.onnx` file. | `--WDModelPath ./models/waifu.onnx` |
| `--WDLabelPath` | Path to the Waifu Diffusion labels file. | `--WDLabelPath ./models/waifu_labels.csv` |
| `--ServiceKey` | Hydrus service key where generated tags will be applied.
| `--Logging:LogLevel:Default` | Set the logging level. | `--Logging:LogLevel:Default Information` |
| `--Services:0:Name` | Name of the first LLM service (used internally). | `--Services:0:Name gpt4-vision` |
| `--Services:0:Endpoint` | Endpoint URL for the LLM API (OpenAI-compatible). | `--Services:0:Endpoint https://api.openai.com/v1` |
| `--Services:0:Key` | API key for the LLM service. | `--Services:0:Key your-openai-key` |
| `--Services:0:Model` | Model name to use (e.g., `gpt-4o`, `llava`). | `--Services:0:Model gpt-4o` |
| `--Services:0:SystemPrompt` | System prompt for the LLM. | `--Services:0:SystemPrompt You are an expert at describing images in detail...` |
| `--Services:0:UserPrompt` | User prompt sent with each image. | `--Services:0:UserPrompt Describe this image in detail...` |

> Up to 3 services can be configured using array-style options like `--Services`.

---

## Configuration Example

```bash
HydrusTagger animated
  --BaseUrl http://localhost:5000
  --HydrusClientAPIAccessKey abcdef123456
  --DDModelPath ./models/deepdanbooru.zip
  --DDLabelPath ./models/tags.txt
  --ServiceKey "4a5e1621-1d11-495a-8f85-a6f554387961"
  --Services:0:Name gpt4v
  --Services:0:Endpoint https://api.openai.com/v1
  --Services:0:Key sk-your-openai-key
  --Services:0:Model gpt-4o
  --Services:0:SystemPrompt "You are an AI that generates descriptive tags for images."
  --Services:0:UserPrompt "Generate comma-separated tags for this image."
  --Logging:LogLevel:Default Information
```

This command:
- Searches for files in Hydrus with the "animated" tag.
- Retrieves each file and extract the middle frame if it's a video
- Generates tags using DeepDanbooru and GPT-4 Vision.
- Applies the combined tags to the specified service.

---

## appsettings.json

You can also configure the app via an `appsettings.json` file, but all settings can be overridden via command-line arguments.

Example `appsettings.json`:
```json
{
  "BaseUrl": "http://localhost:45869",
  "HydrusClientAPIAccessKey": "your-api-key",
  "ServiceKey": "your-service-key",
  "DDModelPath": "path/to/deepdanbooru/model.onnx",
  "DDLabelPath": "path/to/deepdanbooru/labels.txt",
  "WDModelPath": "path/to/waifu/model.onnx",
  "WDLabelPath": "path/to/waifu/labels.csv",
  "Services": [
    {
      "Name": "caption-service",
      "Endpoint": "https://api.openai.com/v1",
      "Key": "your-openai-key",
      "Model": "gpt-4-vision-preview",
      "SystemPrompt": "You are an AI that generates descriptive tags for images.",
      "UserPrompt": "Generate comma-separated tags for this image."
    }
  ],
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

---

## Building Prerequisites

- [.NET 9.0 SDK](https://dotnet.microsoft.com/download) or later

## Building

1. Clone the repository:
```bash
git clone https://github.com/RecapAnon/hydrus-tagger.git
cd hydrus-tagger
```

2. Restore NuGet packages:
```bash
dotnet restore
```

3. Build the project:
```bash
dotnet build
```

4. Run the project:
```bash
dotnet run
```

5. Publish the project:
```bash
dotnet publish -r win-x64
```
Replace `win-x64` with your target runtime (e.g., `linux-x64`, `osx-x64`) as needed.

---

## License

MIT License

## Contributing

Pull requests are welcome. For major changes, please open an issue first to discuss what you would like to change.

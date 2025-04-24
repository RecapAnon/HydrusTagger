module HydrusTagger.Program

open CommandLineExtensions
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open OpenAI
open System
open System.CommandLine
open System.IO
open System.Reflection
open System.Threading.Tasks

type Service =
    { Name: string
      Endpoint: string
      Key: string
      Model: string }

type LogLevelConfig = { Default: LogLevel }

type LoggingConfig = { LogLevel: LogLevelConfig }

type AppSettings =
    { BaseUrl: string
      HydrusClientAPIAccessKey: string
      ServiceKey: string
      ResnetModelPath: string
      Logging: LoggingConfig
      Services: Service array }

let captionNodeApi (kernel: Kernel) (bytes: byte array) =
    let loggerFactory = kernel.Services.GetRequiredService<ILoggerFactory>()
    let logger = loggerFactory.CreateLogger()

    let chat =
        kernel.Services.GetRequiredKeyedService<IChatCompletionService>("Multimodal")

    let history = new ChatHistory()
    history.AddSystemMessage("You are a friendly and helpful assistant that responds to questions directly.")

    let message = new ChatMessageContentItemCollection()
    message.Add(new TextContent("Describe what is in the image."))

    // TODO:
    // let mimeType = getMimeType file
    let mimeType = "temp"
    message.Add(new ImageContent(bytes, mimeType))
    history.AddUserMessage(message)

    let result = chat.GetChatMessageContentAsync(history).Result
    logger.LogInformation("Generation complete: {GeneratorResponse}", result.Content)

    Some result.Content

let handler kernel appSettings (tags: string[]) : Task =
    let hydrusClient =
        HydrusApiClient(appSettings.BaseUrl, appSettings.HydrusClientAPIAccessKey)

    let tagger = DeepdanbooruTagger.Create(appSettings.ResnetModelPath)

    task {
        let! fileIdsResponse = hydrusClient.GetFiles(List.ofArray tags)

        match fileIdsResponse with
        | Some response ->
            for fileId in response.file_ids do
                let! filePathResponse = hydrusClient.GetFilePath(fileId)

                let! fileBytes =
                    match filePathResponse with
                    | Some pathResponse ->
                        printfn "Path for file_id %i: %s" fileId pathResponse.path
                        File.ReadAllBytesAsync pathResponse.path
                    | None -> hydrusClient.DownloadFile(fileId)

                if not (Array.isEmpty fileBytes) then
                    printfn "File downloaded for file_id %i. Size: %i bytes" fileId fileBytes.Length

                let newTags = tagger.Identify fileBytes
                printfn "Tags: %A" newTags

                let request =
                    { AddTagsRequest.file_id = fileId
                      service_keys_to_tags = Map.ofList [ (appSettings.ServiceKey, newTags) ] }

                do! hydrusClient.AddTags(request)
        | None -> printfn "Failed to retrieve file ids."
    }

[<EntryPoint>]
let main argv =
    let appSettings =
        (new ConfigurationBuilder())
            .AddJsonFile("appsettings.json")
            .AddUserSecrets(Assembly.GetExecutingAssembly())
            .AddCommandLine(argv)
            .Build()
            .Get<AppSettings>()

    let kernel =
        let aiClient service =
            let clientOptions = new OpenAIClientOptions()
            clientOptions.Endpoint <- new Uri(service.Endpoint)
            clientOptions.NetworkTimeout <- new TimeSpan(2, 0, 0)

            new OpenAIClient(new ClientModel.ApiKeyCredential(service.Key), clientOptions)

        let builder = Kernel.CreateBuilder()

        builder.Services.AddLogging(fun c ->
            c.AddConsole().SetMinimumLevel(appSettings.Logging.LogLevel.Default) |> ignore)
        |> ignore

        appSettings.Services
        |> Array.iter (fun service ->
            builder.AddOpenAIChatCompletion(service.Model, aiClient service, service.Name)
            |> ignore)

        builder.Build()

    let argument1 = Argument<string[]> "tags"
    let handler1 = handler kernel appSettings

    RootCommand()
    |> addGlobalOption (Option<string> "--BaseUrl")
    |> addGlobalOption (Option<string> "--HydrusClientAPIAccessKey")
    |> addGlobalOption (Option<string> "--ResNetModelPath")
    |> addGlobalOption (Option<string> "--ServiceKey")
    |> addGlobalOption (Option<LogLevel> "--Logging:LogLevel:Default")
    |> addGlobalArgument argument1
    |> setGlobalHandler handler1 argument1
    |> invoke argv

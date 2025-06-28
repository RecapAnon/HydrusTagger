module HydrusTagger.Program

open CommandLineExtensions
open HydrusAPI.NET.Client
open HydrusAPI.NET.Extensions
open HydrusAPI.NET.Api
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open OpenAI
open System
open System.CommandLine
open System.CommandLine.Binding
open System.IO
open System.Linq
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

let handler appSettings (logger: ILogger) (tags: string[]) (api: IGetFilesApi) : Task =
    let hydrusClient =
        HydrusApiClient(appSettings.BaseUrl, appSettings.HydrusClientAPIAccessKey)

    let tagger = DeepdanbooruTagger.Create(appSettings.ResnetModelPath)

    task {
        let! response = api.GetFilesSearchFilesAsync(tags.ToList())

        if response.IsSuccessStatusCode then
            let data = response.Ok()

            for fileId in data.FileIds do
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
                ()
        else
            printfn "Failed to retrieve file ids."
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

    let host =
        Host
            .CreateDefaultBuilder(argv)
            .ConfigureHydrusApi(fun context collection options ->
                let accessToken =
                    new ApiKeyToken(
                        appSettings.HydrusClientAPIAccessKey,
                        ClientUtils.ApiKeyHeader.Hydrus_Client_API_Access_Key,
                        ""
                    )

                let sessionToken =
                    new ApiKeyToken("", ClientUtils.ApiKeyHeader.Hydrus_Client_API_Session_Key, "")

                options.AddTokens<ApiKeyToken>([| accessToken; sessionToken |]) |> ignore
                options.UseProvider<CustomTokenProvider<ApiKeyToken>, ApiKeyToken>() |> ignore

                options.AddHydrusApiHttpClients(
                    (fun client -> client.BaseAddress <- new Uri(appSettings.BaseUrl)),
                    (fun builder ->
                        builder
                            .AddRetryPolicy(2)
                            .AddTimeoutPolicy(TimeSpan.FromSeconds(5L))
                            .AddCircuitBreakerPolicy(10, TimeSpan.FromSeconds(30L))
                        |> ignore)
                )
                |> ignore

                ())
            .Build()

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

    let loggerBinder =
        { new BinderBase<ILogger>() with
            override _.GetBoundValue(bindingContext) =
                let loggerFactory = kernel.Services.GetRequiredService<ILoggerFactory>()
                loggerFactory.CreateLogger() }

    let argument1 = Argument<string[]> "tags"
    let handler1 = handler appSettings

    RootCommand()
    |> addGlobalOption (Option<string> "--BaseUrl")
    |> addGlobalOption (Option<string> "--HydrusClientAPIAccessKey")
    |> addGlobalOption (Option<string> "--ResNetModelPath")
    |> addGlobalOption (Option<string> "--ServiceKey")
    |> addGlobalOption (Option<LogLevel> "--Logging:LogLevel:Default")
    |> addGlobalArgument argument1
    |> setGlobalHandler3 handler1 loggerBinder argument1 (srvBinder<IGetFilesApi> host)
    |> invoke argv

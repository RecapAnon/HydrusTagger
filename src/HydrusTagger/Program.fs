module HydrusTagger.Program

open CommandLineExtensions
open HydrusAPI.NET.Client
open HydrusAPI.NET.Extensions
open HydrusAPI.NET.Api
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open OpenAI
open System
open System.CommandLine
open System.IO
open System.Linq
open System.Threading.Tasks

type Service =
    { Name: string
      Endpoint: string
      Key: string
      Model: string }

type LogLevelConfig = { Default: LogLevel }

type LoggingConfig = { LogLevel: LogLevelConfig }

[<CLIMutable>]
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

let handler (tags: string[]) (options: IOptions<AppSettings>) (logger: ILogger) (api: IGetFilesApi) : Task =
    let appSettings = options.Value

    let hydrusClient =
        HydrusApiClient(appSettings.BaseUrl, appSettings.HydrusClientAPIAccessKey)

    let tagger = DeepdanbooruTagger.Create(appSettings.ResnetModelPath)

    task {
        let! response = api.GetFilesSearchFilesAsync(tags.ToList())

        if response.IsSuccessStatusCode then
            let data = response.Ok()

            for fileId in data.FileIds do
                let! filePathResponse = api.GetFilesFilePathOrDefaultAsync(fileId)

                let! fileBytes =
                    if filePathResponse.IsSuccessStatusCode then
                        let pathResponse = filePathResponse.Ok()
                        logger.LogInformation("Path for file_id {FileId}: {Path}", fileId, pathResponse.Path)

                        logger.LogInformation(
                            "Content Type for file_id {FileId}: {ContentType}",
                            fileId,
                            pathResponse.Filetype
                        )

                        File.ReadAllBytesAsync pathResponse.Path
                    else
                        task {
                            let! resp = api.GetFilesFileAsync(new HydrusAPI.NET.Client.Option<Nullable<int>>(fileId))
                            let r = resp :?> GetFilesApi.GetFilesFileApiResponse

                            logger.LogInformation(
                                "Content Type for file_id {FileId}: {ContentType}",
                                fileId,
                                r.ContentHeaders.ContentType
                            )

                            return r.ContentBytes
                        }

                if not (Array.isEmpty fileBytes) then
                    logger.LogInformation(
                        "File downloaded for file_id {FileId}. Size: {Size} bytes",
                        fileId,
                        fileBytes.Length
                    )

                let newTags = tagger.Identify fileBytes
                logger.LogInformation("Tags: {Tags}", newTags)

                let request =
                    { AddTagsRequest.file_id = fileId
                      service_keys_to_tags = Map.ofList [ (appSettings.ServiceKey, newTags) ] }

                do! hydrusClient.AddTags(request)
                ()
        else
            logger.LogError("Failed to retrieve file ids.")
    }

[<EntryPoint>]
let main argv =
    let host =
        Host
            .CreateDefaultBuilder(argv)
            .ConfigureServices(fun context services ->
                let appSettings = context.Configuration.Get<AppSettings>()
                services.Configure<AppSettings>(context.Configuration) |> ignore

                services.AddLogging(fun c ->
                    c.AddConsole().SetMinimumLevel(appSettings.Logging.LogLevel.Default) |> ignore)
                |> ignore

                appSettings.Services
                |> Array.iter (fun service ->
                    let client =
                        let clientOptions = new OpenAIClientOptions()
                        clientOptions.Endpoint <- new Uri(service.Endpoint)
                        clientOptions.NetworkTimeout <- new TimeSpan(2, 0, 0)

                        new OpenAIClient(new ClientModel.ApiKeyCredential(service.Key), clientOptions)

                    services.AddOpenAIChatCompletion(service.Model, client, service.Name) |> ignore)

                services.AddTransient<Kernel>(fun (serviceProvider) ->
                    let pluginCollection = serviceProvider.GetRequiredService<KernelPluginCollection>()
                    new Kernel(serviceProvider, pluginCollection))
                |> ignore)
            .ConfigureHydrusApi(fun context collection options ->
                let appSettings = context.Configuration.Get<AppSettings>()

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

    let argument1 = Argument<string[]> "tags"

    RootCommand()
    |> addGlobalOption (Option<string> "--BaseUrl")
    |> addGlobalOption (Option<string> "--HydrusClientAPIAccessKey")
    |> addGlobalOption (Option<string> "--ResNetModelPath")
    |> addGlobalOption (Option<string> "--ServiceKey")
    |> addGlobalOption (Option<LogLevel> "--Logging:LogLevel:Default")
    |> addGlobalArgument argument1
    |> setGlobalHandler4
        handler
        argument1
        (srvBinder<IOptions<AppSettings>> host)
        (srvBinder<ILogger<AppSettings>> host)
        (srvBinder<IGetFilesApi> host)
    |> invoke argv

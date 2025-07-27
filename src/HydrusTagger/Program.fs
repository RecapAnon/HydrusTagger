module HydrusTagger.Program

open CommandLineExtensions
open FsToolkit.ErrorHandling
open HydrusAPI.NET.Api
open HydrusAPI.NET.Client
open HydrusAPI.NET.Extensions
open HydrusAPI.NET.Model
open HydrusApiWrapper
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open Microsoft.SemanticKernel.Connectors.OpenAI
open OpenAI
open System
open System.Collections.Generic
open System.CommandLine
open System.IO
open System.Linq
open System.Threading.Tasks
open VideoFrameExtractor

type ApiOption<'T> = HydrusAPI.NET.Client.Option<'T>

type Service =
    { Name: string
      Endpoint: string
      Key: string
      Model: string
      SystemPrompt: string
      UserPrompt: string
      ExecutionSettings: OpenAIPromptExecutionSettings }

type LogLevelConfig = { Default: LogLevel }

type LoggingConfig = { LogLevel: LogLevelConfig }

[<CLIMutable>]
type AppSettings =
    { BaseUrl: string | null
      HydrusClientAPIAccessKey: string | null
      ServiceKey: string | null
      ResnetModelPath: string | null
      Logging: LoggingConfig | null
      Services: Service array | null }

let captionApi (kernel: Kernel) (service: Service) (logger: ILogger) (bytes: byte array) (mimeType: string) =
    taskResult {
        let history = new ChatHistory()
        history.AddSystemMessage(service.SystemPrompt)

        let message = new ChatMessageContentItemCollection()
        message.Add(new TextContent(service.UserPrompt))
        message.Add(new ImageContent(bytes, mimeType))
        history.AddUserMessage(message)

        let! chat =
            kernel.Services.GetKeyedService<IChatCompletionService>(service.Name)
            |> Result.requireNotNull "Error"

        let! result =
            tryCall logger "GetChatMessageContent" (fun () ->
                chat.GetChatMessageContentAsync(history, service.ExecutionSettings))

        return! result.Content |> Result.requireNotNull "Error"
    }

let handler
    (tags: string[])
    (options: IOptions<AppSettings>)
    (logger: ILogger)
    (kernel: Kernel)
    (getFilesApi: IGetFilesApi)
    (addTagsApi: IAddTagsApi)
    : Task =
    task {
        let appSettings = options.Value

        let tagger =
            match appSettings.ResnetModelPath with
            | null -> None
            | path -> Some(DeepdanbooruTagger.Create(path))

        let! fileIdsResult =
            tryCallHydrusApi logger "GetFilesSearchFiles" (fun () ->
                getFilesApi.GetFilesSearchFilesAsync(tags.ToList()))

        let fileIdsResponse = fileIdsResult |> getApiResponseData

        match fileIdsResponse with
        | Ok data ->
            for fileId in data.FileIds do
                let! filePathResult =
                    tryCallHydrusApi logger "GetFilesFilePathOrDefault" (fun () ->
                        getFilesApi.GetFilesFilePathOrDefaultAsync(fileId))

                let filePathResponse = filePathResult |> getApiResponseData

                let! fileBytes, fileType =
                    match filePathResponse with
                    | Ok pathResponse ->
                        task {
                            logger.LogInformation("Path for file_id {FileId}: {Path}", fileId, pathResponse.Path)
                            let! bytes = File.ReadAllBytesAsync(pathResponse.Path)
                            return bytes, pathResponse.Filetype
                        }
                    | Error _ ->
                        task {
                            let! result =
                                tryCallHydrusApi logger "GetFilesFile" (fun () ->
                                    getFilesApi.GetFilesFileAsync(new ApiOption<Nullable<int>>(fileId)))

                            // TODO:
                            match result with
                            | Ok response ->
                                let response2 = response :?> GetFilesApi.GetFilesFileApiResponse
                                return response2.ContentBytes, response2.ContentHeaders.ContentType.ToString()
                            | Error _ -> return [||], ""
                        }

                if not (Array.isEmpty fileBytes) then
                    logger.LogInformation(
                        "File downloaded for file_id {FileId}. Type: {Type} Size: {Size} bytes",
                        fileId,
                        fileType,
                        fileBytes.Length
                    )

                let actualBytes =
                    if fileType.Contains("video") then
                        let tmp = Path.GetTempFileName()
                        File.WriteAllBytes(tmp, fileBytes)
                        let bytes = getMiddleFrameBytes tmp
                        File.Delete(tmp)
                        bytes
                    else
                        fileBytes

                let newTags =
                    match tagger with
                    | Some t -> t.Identify actualBytes
                    | None -> [||]

                logger.LogInformation("DeepDanbooru Tags: {Tags}", newTags)

                let! captions =
                    match appSettings.Services with
                    | null -> Task.FromResult([||])
                    | srv ->
                        srv
                        |> Array.map (fun service ->
                            task {
                                let! result = captionApi kernel service logger actualBytes fileType

                                match result with
                                | Ok caption ->
                                    logger.LogInformation("{ServiceName} Tags: {Tags}", service.Name, caption)

                                    return
                                        caption.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                                        |> Array.map (fun s -> s.Trim())
                                | Error message ->
                                    logger.LogError("{ServiceName} failed: {GeneratorError}", message)
                                    return [||]
                            })
                        |> Task.WhenAll

                let allTags = captions |> Array.collect id |> Array.append newTags |> Array.distinct

                let serviceKeysToTags = Dictionary<string, List<string>>()

                match appSettings.ServiceKey with
                | null -> ()
                | serviceKey -> serviceKeysToTags[serviceKey] <- allTags.ToList()

                let request =
                    AddTagsAddTagsRequest(
                        fileId = new ApiOption<Nullable<int>>(fileId),
                        serviceKeysToTags = new ApiOption<Dictionary<string, List<string>>>(serviceKeysToTags)
                    )

                let! _ = tryCallHydrusApi logger "AddTagsAddTags" (fun () -> addTagsApi.AddTagsAddTagsAsync(request))

                ()
        | Error message -> logger.LogError("Failed to retrieve file ids: {Error}", message)
    }

[<EntryPoint>]
let main argv =
    let host =
        Host
            .CreateDefaultBuilder(argv)
            .ConfigureServices(fun context services ->
                let appSettings = context.Configuration.Get<AppSettings>()

                match appSettings.Services with
                | null -> ()
                | srv ->
                    srv
                    |> Array.iter (fun service ->
                        let client =
                            let clientOptions = new OpenAIClientOptions()
                            clientOptions.Endpoint <- new Uri(service.Endpoint)
                            clientOptions.NetworkTimeout <- new TimeSpan(2, 0, 0)

                            new OpenAIClient(new ClientModel.ApiKeyCredential(service.Key), clientOptions)

                        services.AddOpenAIChatCompletion(service.Model, client, service.Name) |> ignore)

                services
                    .Configure<AppSettings>(context.Configuration)
                    .AddLogging(fun c -> c.AddConsole() |> ignore)
                    .AddSingleton<KernelPluginCollection>(fun serviceProvider -> new KernelPluginCollection())
                    .AddTransient<Kernel>(fun serviceProvider ->
                        let pluginCollection = serviceProvider.GetRequiredService<KernelPluginCollection>()
                        new Kernel(serviceProvider, pluginCollection))
                |> ignore)
            .ConfigureHydrusApi(fun context collection options ->
                let appSettings = context.Configuration.Get<AppSettings>()

                let accessToken =
                    match appSettings.HydrusClientAPIAccessKey with
                    | null -> new ApiKeyToken("", ClientUtils.ApiKeyHeader.Hydrus_Client_API_Access_Key, "")
                    | key -> new ApiKeyToken(key, ClientUtils.ApiKeyHeader.Hydrus_Client_API_Access_Key, "")

                let sessionToken =
                    new ApiKeyToken("", ClientUtils.ApiKeyHeader.Hydrus_Client_API_Session_Key, "")

                options
                    .AddTokens<ApiKeyToken>([| accessToken; sessionToken |])
                    .AddHydrusApiHttpClients(
                        (fun client ->
                            if appSettings.BaseUrl <> null then
                                client.BaseAddress <- new Uri(appSettings.BaseUrl)),
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
    |> addGlobalOption (Option<Service array> "--Services")
    |> addGlobalArgument argument1
    |> setGlobalHandler6
        handler
        argument1
        (srvBinder<IOptions<AppSettings>> host)
        (srvBinder<ILogger<AppSettings>> host)
        (srvBinder<Kernel> host)
        (srvBinder<IGetFilesApi> host)
        (srvBinder<IAddTagsApi> host)
    |> invoke argv

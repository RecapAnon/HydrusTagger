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
open Microsoft.SemanticKernel
open Microsoft.SemanticKernel.ChatCompletion
open OpenAI
open ResultExtensions
open System
open System.Collections.Generic
open System.CommandLine
open System.IO
open System.Linq
open System.Threading.Tasks
open VideoFrameExtractor

type ApiOption<'T> = HydrusAPI.NET.Client.Option<'T>

let captionApi (kernel: Kernel) (service: TaggingService) (logger: ILogger) (bytes: byte array) (mimeType: string) =
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

let getFileBytesAndType
    (logger: ILogger)
    (getFilesApi: IGetFilesApi)
    (fileId: int)
    : Task<Result<byte array * string, string>> =
    taskResult {
        let! filePathResponse =
            tryCallHydrusApi logger "GetFilesFilePathOrDefault" (fun () ->
                getFilesApi.GetFilesFilePathOrDefaultAsync(fileId))

        match getOk filePathResponse with
        | Ok pathResponse ->
            logger.LogInformation("Path for file_id {FileId}: {Path}", fileId, pathResponse.Path)
            let! path = Result.requireNotNull "null" pathResponse.Path
            let! filetype = Result.requireNotNull "null" pathResponse.Filetype
            let! bytes = File.ReadAllBytesAsync(path)

            if Array.isEmpty bytes then
                return! Error $"Failed to read file content from path for file_id {fileId}"
            else
                return! Ok(bytes, filetype)
        | Error _ ->
            let! fileResponse =
                tryCallHydrusApi logger "GetFilesFile" (fun () ->
                    getFilesApi.GetFilesFileAsync(new ApiOption<Nullable<int>>(fileId)))

            let response = fileResponse :?> GetFilesApi.GetFilesFileApiResponse
            let bytes = response.ContentBytes
            let! contentType = Result.requireNotNull "null" response.ContentHeaders.ContentType

            if Array.isEmpty bytes then
                return! Error $"Failed to retrieve file content for file_id {fileId}"
            else
                return! Ok(bytes, contentType.ToString())
    }

let extractFrameIfVideo (logger: ILogger) (fileBytes: byte array) (fileType: string) : byte array =
    if fileType.Contains("video") then
        let tmp = Path.GetTempFileName()
        File.WriteAllBytes(tmp, fileBytes)
        let extracted = getMiddleFrameBytes tmp
        File.Delete(tmp)
        logger.LogInformation("Extracted middle frame from video for tagging")
        extracted
    else
        fileBytes

let getDeepDanbooruTags (logger: ILogger) (tagger: DeepdanbooruPredictor option) (bytes: byte array) : string array =
    match tagger with
    | Some t ->
        let tags = t.predict bytes
        logger.LogInformation("DeepDanbooru Tags: {Tags}", tags)
        tags
    | None -> [||]

let getWaifuTags (logger: ILogger) (tagger: WaifuDiffusionPredictor option) (bytes: byte array) : string array =
    match tagger with
    | Some t ->
        let result = t.predict bytes 0.35 true 0.85 true
        let tags = result.GeneralTags |> Array.map fst
        logger.LogInformation("WaifuDiffusion Tags: {Tags}", tags)
        tags
    | None -> [||]

let getCaptionTags
    (logger: ILogger)
    (kernel: Kernel)
    (services: TaggingService[] option)
    (bytes: byte array)
    (mimeType: string)
    : Task<string array> =
    services
    |> Option.defaultValue [||]
    |> Array.map (fun service ->
        task {
            let! result = captionApi kernel service logger bytes mimeType

            match result with
            | Ok caption ->
                logger.LogInformation("{ServiceName} Tags: {Tags}", service.Name, caption)

                return
                    caption.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
                    |> Array.map (fun s -> s.Trim())
            | Error err ->
                logger.LogError("{ServiceName} failed: {Error}", service.Name, err)
                return [||]
        })
    |> Task.WhenAll
    |> Task.map (Array.collect id)

let applyTagsToHydrusFile
    (logger: ILogger)
    (addTagsApi: IAddTagsApi)
    (fileId: int)
    (config: IAppConfig)
    (allTags: string array)
    : Task<unit> =
    task {
        let serviceKeysToTags = Dictionary<string, List<string>>()

        match config.ServiceKey with
        | Some serviceKey -> serviceKeysToTags[serviceKey] <- allTags.ToList()
        | None -> ()

        let request =
            AddTagsAddTagsRequest(
                fileId = ApiOption<Nullable<int>>(fileId),
                serviceKeysToTags = ApiOption<Dictionary<string, List<string>>>(serviceKeysToTags)
            )

        let! result = tryCallHydrusApi logger "AddTagsAddTags" (fun () -> addTagsApi.AddTagsAddTagsAsync(request))

        match result with
        | Error err -> logger.LogError("Failed to apply tags to file_id {FileId}: {Error}", fileId, err)
        | Ok _ -> ()
    }

let handler
    (tags: string[])
    (config: IAppConfig)
    (logger: ILogger)
    (kernel: Kernel)
    (getFilesApi: IGetFilesApi)
    (addTagsApi: IAddTagsApi)
    : Task =
    task {
        let tagger =
            try
                match config.ResnetModelPath, config.ResnetLabelPath with
                | Some modelPath, Some labelPath -> Some(new DeepdanbooruPredictor(modelPath, labelPath))
                | _ ->
                    logger.LogWarning("Failed to initialize DeepdanbooruPredictor: Missing configuration.")
                    None
            with ex ->
                logger.LogError("Failed to initialize DeepdanbooruPredictor: {Error}", ex.Message)
                None

        let waifuTagger =
            try
                match config.WaifuModelPath, config.WaifuLabelPath with
                | Some modelPath, Some labelPath -> Some(new WaifuDiffusionPredictor(modelPath, labelPath))
                | _ ->
                    logger.LogWarning("Failed to initialize WaifuDiffusionPredictor: Missing configuration.")
                    None
            with ex ->
                logger.LogError("Failed to initialize WaifuDiffusionPredictor: {Error}", ex.Message)
                None

        let! result =
            taskResult {
                let! fileIdsResponse =
                    tryCallHydrusApi logger "GetFilesSearchFiles" (fun () ->
                        getFilesApi.GetFilesSearchFilesAsync(tags.ToList()))

                let! fileIds =
                    fileIdsResponse
                    |> getOk
                    |> Result.map (fun r -> r.FileIds)
                    |> Result.bind (Result.requireNotNull "null")

                for fileId in fileIds do
                    let! fileBytes, fileType = getFileBytesAndType logger getFilesApi fileId
                    let actualBytes = extractFrameIfVideo logger fileBytes fileType
                    let ddTags = getDeepDanbooruTags logger tagger actualBytes
                    let waifuTags = getWaifuTags logger waifuTagger actualBytes
                    let! captionTags = getCaptionTags logger kernel config.Services actualBytes fileType
                    let allTags = Array.concat [| ddTags; waifuTags; captionTags |] |> Array.distinct
                    do! applyTagsToHydrusFile logger addTagsApi fileId config allTags
            }

        tagger |> Option.iter (fun t -> (t :> IDisposable).Dispose())
        waifuTagger |> Option.iter (fun t -> (t :> IDisposable).Dispose())
        return result
    }

[<EntryPoint>]
let main argv =
    let host =
        Host
            .CreateDefaultBuilder(argv)
            .ConfigureServices(fun context services ->
                let appSettings = context.Configuration.Get<AppSettings>() |> Option.ofObj

                match appSettings with
                | Some settings ->
                    let appConfig = new AppConfig(settings) :> IAppConfig

                    match appConfig.Services with
                    | Some servicesArray ->
                        servicesArray
                        |> Array.iter (fun service ->
                            let clientOptions = new OpenAIClientOptions()
                            clientOptions.Endpoint <- Uri(service.Endpoint)
                            clientOptions.NetworkTimeout <- TimeSpan(2, 0, 0)

                            let client =
                                new OpenAIClient(ClientModel.ApiKeyCredential(service.Key), clientOptions)

                            services.AddOpenAIChatCompletion(service.Model, client, service.Name) |> ignore)
                    | None -> ()

                    services
                        .Configure<AppSettings>(context.Configuration)
                        .AddLogging(fun c -> c.AddConsole() |> ignore)
                        .AddSingleton<IAppConfig, AppConfig>()
                        .AddSingleton<KernelPluginCollection>(fun _ -> KernelPluginCollection())
                        .AddTransient<Kernel>(fun serviceProvider ->
                            let pluginCollection = serviceProvider.GetRequiredService<KernelPluginCollection>()
                            Kernel(serviceProvider, pluginCollection))
                    |> ignore
                | None -> ()

                ())
            .ConfigureHydrusApi(fun context collection options ->
                let appSettings = context.Configuration.Get<AppSettings>() |> Option.ofObj

                match appSettings with
                | Some settings ->
                    let appConfig = new AppConfig(settings) :> IAppConfig

                    let accessToken =
                        match appConfig.HydrusClientAPIAccessKey with
                        | Some key -> new ApiKeyToken(key, ClientUtils.ApiKeyHeader.Hydrus_Client_API_Access_Key, "")
                        | None -> new ApiKeyToken("", ClientUtils.ApiKeyHeader.Hydrus_Client_API_Access_Key, "")

                    let sessionToken =
                        new ApiKeyToken("", ClientUtils.ApiKeyHeader.Hydrus_Client_API_Session_Key, "")

                    let baseUrl = defaultArg (appConfig.BaseUrl) "localhost:45869"

                    options
                        .AddTokens<ApiKeyToken>([| accessToken; sessionToken |])
                        .AddHydrusApiHttpClients(
                            (fun client -> client.BaseAddress <- Uri(baseUrl)),
                            (fun builder ->
                                builder
                                    .AddRetryPolicy(2)
                                    .AddTimeoutPolicy(TimeSpan.FromSeconds(5L))
                                    .AddCircuitBreakerPolicy(10, TimeSpan.FromSeconds(30L))
                                |> ignore)
                        )
                    |> ignore
                | None -> ()

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
        (srvBinder<IAppConfig> host)
        (srvBinder<ILogger<AppSettings>> host)
        (srvBinder<Kernel> host)
        (srvBinder<IGetFilesApi> host)
        (srvBinder<IAddTagsApi> host)
    |> invoke argv

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
open System.Diagnostics
open System.IO
open System.Linq
open System.Net.Http
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
            |> Result.requireNotNull "Chat completion service not found in kernel services"

        let! result =
            tryCall logger "GetChatMessageContent" (fun () ->
                chat.GetChatMessageContentAsync(history, service.ExecutionSettings))

        return! result.Content |> Result.requireNotNull "Chat completion result content is null"
    }

let getFileBytesAndType (logger: ILogger) (getFilesApi: IGetFilesApi) (fileId: int) =
    task {
        let! filePathResponse =
            tryCallHydrusApi logger "GetFilesFilePathOrDefault" (fun () ->
                getFilesApi.GetFilesFilePathOrDefaultAsync(fileId))

        return!
            taskResult {
                match filePathResponse |> Result.bind getOk with
                | Ok pathResponse ->
                    logger.LogInformation("Path for file_id {FileId}: {Path}", fileId, pathResponse.Path)
                    let! path = Result.requireNotNull "File path is null in path response" pathResponse.Path
                    let! filetype = Result.requireNotNull "File type is null in path response" pathResponse.Filetype
                    let! bytes = File.ReadAllBytesAsync(path)

                    if Array.isEmpty bytes then
                        return! Error $"Failed to read file content from path for file_id {fileId}"
                    else
                        return! Ok(bytes, filetype)
                | Error _ ->
                    let! fileResponse =
                        tryCallHydrusApi logger "GetFilesFile" (fun () ->
                            getFilesApi.GetFilesFileAsync(new ApiOption<int>(fileId)))

                    let response = fileResponse :?> GetFilesApi.GetFilesFileApiResponse
                    let bytes = response.ContentBytes

                    let! contentType =
                        Result.requireNotNull
                            "Content type is null in response headers"
                            response.ContentHeaders.ContentType

                    if Array.isEmpty bytes then
                        return! Error $"Failed to retrieve file content for file_id {fileId}"
                    else
                        return! Ok(bytes, contentType.ToString())
            }
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
        let ratingTags = result.RatingTags |> Array.map fst
        let generalTags = result.GeneralTags |> Array.map fst
        let characterTags = result.CharacterTags |> Array.map fst
        let allTags = Array.concat [ ratingTags; generalTags; characterTags ]
        logger.LogInformation("WaifuDiffusion Tags: {Tags}", allTags)
        allTags
    | None -> [||]

let getCaptionTags
    (logger: ILogger)
    (kernel: Kernel)
    (services: TaggingService[] option)
    (bytes: byte array)
    (mimeType: string)
    : Task<string array> =
    let kaomojis =
        [ "0_0"
          "(o)_(o)"
          "+_+"
          "+_-"
          "._."
          "<o>_<o>"
          "<|>_<|>"
          "=_="
          ">_<"
          "3_3"
          "6_9"
          ">_o"
          "@_@"
          "^_^"
          "o_o"
          "u_u"
          "x_x"
          "|_|"
          "||_||" ]
        |> set

    let formatName (name: string) =
        if kaomojis.Contains(name) then
            name
        else
            name.Replace("_", " ")

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
                    |> Array.map (fun s -> s.Trim() |> formatName)
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

        let convertDict (inputDict: Dictionary<string, List<string>>) : Dictionary<string, obj> =
            let outputDict = new Dictionary<string, obj>()
            inputDict |> Seq.iter (fun kvp -> outputDict.Add(kvp.Key, kvp.Value :> obj))
            outputDict

        let request =
            AddTagsAddTagsRequest(
                fileId = ApiOption<Nullable<int>>(fileId),
                serviceKeysToTags = ApiOption<Dictionary<string, obj>>(convertDict serviceKeysToTags)
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
                match config.DDModelPath, config.DDLabelPath, config.DDCharacterLabelPath with
                | Some modelPath, Some labelPath, Some characterLabelPath ->
                    Some(new DeepdanbooruPredictor(modelPath, labelPath, characterLabelPath, config.UseCuda))
                | _ ->
                    logger.LogWarning("Failed to initialize DeepdanbooruPredictor: Missing configuration.")
                    None
            with ex ->
                logger.LogError(ex, "Failed to initialize DeepdanbooruPredictor: {Error}", ex.Message)
                None

        let waifuTagger =
            try
                match config.WDModelPath, config.WDLabelPath with
                | Some modelPath, Some labelPath ->
                    Some(new WaifuDiffusionPredictor(modelPath, labelPath, config.UseCuda))
                | _ ->
                    logger.LogWarning("Failed to initialize WaifuDiffusionPredictor: Missing configuration.")
                    None
            with ex ->
                logger.LogError(ex, "Failed to initialize WaifuDiffusionPredictor: {Error}", ex.Message)
                None

        let totalStopwatch = Stopwatch.StartNew()

        let! result =
            taskResult {
                let! fileIdsResponse =
                    tryCallHydrusApi logger "GetFilesSearchFiles" (fun () ->
                        getFilesApi.GetFilesSearchFilesAsync(tags.ToList()))

                let! fileIds =
                    fileIdsResponse
                    |> getOk
                    |> Result.map (fun r -> r.FileIds)
                    |> Result.bind (Result.requireNotNull "File IDs are null in response")

                let totalFiles = fileIds.Count
                logger.LogInformation("Found {TotalFiles} files to process", totalFiles)

                for i = 0 to totalFiles - 1 do
                    let fileId = fileIds.[i]
                    let fileProgress = i + 1

                    logger.LogInformation(
                        "Processing file {FileProgress} of {TotalFiles} (file_id: {FileId})",
                        fileProgress,
                        totalFiles,
                        fileId
                    )

                    let fileStopwatch = Stopwatch.StartNew()

                    do!
                        taskResult {
                            let! fileBytes, fileType = getFileBytesAndType logger getFilesApi fileId
                            let getFileTime = fileStopwatch.ElapsedMilliseconds
                            logger.LogInformation("Time to retrieve file {FileId}: {Time}ms", fileId, getFileTime)

                            let actualBytes = extractFrameIfVideo logger fileBytes fileType
                            let extractTime = fileStopwatch.ElapsedMilliseconds - getFileTime

                            logger.LogInformation(
                                "Time to extract frame from file {FileId}: {Time}ms",
                                fileId,
                                extractTime
                            )

                            let ddTags = getDeepDanbooruTags logger tagger actualBytes
                            let ddTime = fileStopwatch.ElapsedMilliseconds - getFileTime - extractTime

                            logger.LogInformation(
                                "Time for DeepDanbooru tagging of file {FileId}: {Time}ms",
                                fileId,
                                ddTime
                            )

                            let waifuTags = getWaifuTags logger waifuTagger actualBytes

                            let waifuTime =
                                fileStopwatch.ElapsedMilliseconds - getFileTime - extractTime - ddTime

                            logger.LogInformation(
                                "Time for WaifuDiffusion tagging of file {FileId}: {Time}ms",
                                fileId,
                                waifuTime
                            )

                            let! captionTags = getCaptionTags logger kernel config.Services actualBytes fileType

                            let captionTime =
                                fileStopwatch.ElapsedMilliseconds
                                - getFileTime
                                - extractTime
                                - ddTime
                                - waifuTime

                            logger.LogInformation(
                                "Time for caption tagging of file {FileId}: {Time}ms",
                                fileId,
                                captionTime
                            )

                            let allTags = Array.concat [| ddTags; waifuTags; captionTags |] |> Array.distinct
                            logger.LogInformation("Total tags for file {FileId}: {TagCount}", fileId, allTags.Length)

                            do! applyTagsToHydrusFile logger addTagsApi fileId config allTags

                            let applyTime =
                                fileStopwatch.ElapsedMilliseconds
                                - getFileTime
                                - extractTime
                                - ddTime
                                - waifuTime
                                - captionTime

                            logger.LogInformation("Time to apply tags to file {FileId}: {Time}ms", fileId, applyTime)
                        }

                    fileStopwatch.Stop()

                    logger.LogInformation(
                        "Total time to process file {FileId}: {TotalTime}ms",
                        fileId,
                        fileStopwatch.ElapsedMilliseconds
                    )
            }

        totalStopwatch.Stop()
        logger.LogInformation("Total time to process all files: {TotalTime}ms", totalStopwatch.ElapsedMilliseconds)

        tagger |> Option.iter (fun t -> (t :> IDisposable).Dispose())
        waifuTagger |> Option.iter (fun t -> (t :> IDisposable).Dispose())

        match result with
        | Error err -> logger.LogError("{Error}", err)
        | Ok _ -> ()

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

                    let clientBuilder =
                        match appConfig.BaseUrl with
                        | Some baseUrl -> Action<HttpClient>(fun client -> client.BaseAddress <- Uri(baseUrl))
                        | None -> Unchecked.defaultof<Action<HttpClient>>

                    options
                        .AddTokens<ApiKeyToken>([| accessToken; sessionToken |])
                        .AddHydrusApiHttpClients(
                            clientBuilder,
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
    let cudaOption = Option<bool> "--UseCuda"
    cudaOption.SetDefaultValue(false)

    RootCommand()
    |> addGlobalOption (Option<string> "--BaseUrl")
    |> addGlobalOption cudaOption
    |> addGlobalOption (Option<string> "--HydrusClientAPIAccessKey")
    |> addGlobalOption (Option<string> "--DDModelPath")
    |> addGlobalOption (Option<string> "--DDLabelPath")
    |> addGlobalOption (Option<string> "--DDCharacterLabelPath")
    |> addGlobalOption (Option<string> "--WDModelPath")
    |> addGlobalOption (Option<string> "--WDLabelPath")
    |> addGlobalOption (Option<string> "--ServiceKey")
    |> addGlobalOption (Option<LogLevel> "--Logging:LogLevel:Default")
    |> addGlobalOption (Option<string> "--Services:0:Name")
    |> addGlobalOption (Option<string> "--Services:0:Endpoint")
    |> addGlobalOption (Option<string> "--Services:0:Key")
    |> addGlobalOption (Option<string> "--Services:0:Model")
    |> addGlobalOption (Option<string> "--Services:0:SystemPrompt")
    |> addGlobalOption (Option<string> "--Services:0:UserPrompt")
    |> addGlobalHiddenOption (Option<string> "--Services:1:Name")
    |> addGlobalHiddenOption (Option<string> "--Services:1:Endpoint")
    |> addGlobalHiddenOption (Option<string> "--Services:1:Key")
    |> addGlobalHiddenOption (Option<string> "--Services:1:Model")
    |> addGlobalHiddenOption (Option<string> "--Services:1:SystemPrompt")
    |> addGlobalHiddenOption (Option<string> "--Services:1:UserPrompt")
    |> addGlobalHiddenOption (Option<string> "--Services:2:Name")
    |> addGlobalHiddenOption (Option<string> "--Services:2:Endpoint")
    |> addGlobalHiddenOption (Option<string> "--Services:2:Key")
    |> addGlobalHiddenOption (Option<string> "--Services:2:Model")
    |> addGlobalHiddenOption (Option<string> "--Services:2:SystemPrompt")
    |> addGlobalHiddenOption (Option<string> "--Services:2:UserPrompt")
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

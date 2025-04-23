open HydrusApi
open DeepdanbooruTagger
open Microsoft.Extensions.Configuration
open System
open System.CommandLine
open System.IO
open System.Reflection
open System.Threading.Tasks

type Command = CommandLine.Command
type Argument<'T> = CommandLine.Argument<'T>
type RootCommand = CommandLine.RootCommand

type AppSettings =
    { BaseUrl: string
      HydrusClientAPIAccessKey: string
      ServiceKey: string
      ResnetModelPath: string }

let addGlobalOption option (command: RootCommand) =
    command.AddGlobalOption option
    command

let addGlobalArgument argument (command: RootCommand) =
    command.AddArgument argument
    command

let setGlobalHandler handler argument (command: RootCommand) =
    command.SetHandler(handler, argument)
    command

let invoke (argv: string array) (rc: RootCommand) = rc.Invoke argv

let handler appSettings (tags: string[]) : Task =
    let hydrusClient =
        HydrusApiClient(appSettings.BaseUrl, appSettings.HydrusClientAPIAccessKey)

    let tagger = Tagger.Create(appSettings.ResnetModelPath)

    task {
        let! fileIdsResponse = hydrusClient.GetFiles(List.ofArray tags)

        match fileIdsResponse with
        | Some response ->
            for fileId in response.file_ids do
                let! filePathResponse = hydrusClient.GetFilePath(fileId)

                match filePathResponse with
                | Some pathResponse ->
                    printfn "Path for file_id %i: %s" fileId pathResponse.path
                    let newTags = tagger.Identify(File.ReadAllBytes pathResponse.path)
                    printfn "Tags: %A" newTags

                    let request =
                        { HydrusApi.AddTagsRequest.file_id = fileId
                          service_keys_to_tags = Map.ofList [ (appSettings.ServiceKey, newTags) ] }

                    do! hydrusClient.AddTags(request)
                | None ->
                    let! fileBytes = hydrusClient.DownloadFile(fileId)

                    if not (Array.isEmpty fileBytes) then
                        printfn "File downloaded for file_id %i. Size: %i bytes" fileId fileBytes.Length
                        let newTags = tagger.Identify fileBytes
                        printfn "Tags: %A" newTags

                        let request =
                            { HydrusApi.AddTagsRequest.file_id = fileId
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

    let argument1 = Argument<string[]> "tags"
    let handler1 = handler appSettings

    RootCommand()
    |> addGlobalOption (Option<string> "--BaseUrl")
    |> addGlobalOption (Option<string> "--HydrusClientAPIAccessKey")
    |> addGlobalOption (Option<string> "--ResNetModelPath")
    |> addGlobalOption (Option<string> "--ServiceKey")
    |> addGlobalArgument argument1
    |> setGlobalHandler handler1 argument1
    |> invoke argv

open Microsoft.Extensions.Configuration
open System
open System.CommandLine
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open System.Reflection

type Command = CommandLine.Command
type Argument<'T> = CommandLine.Argument<'T>
type RootCommand = CommandLine.RootCommand

type AppSettings = {
    BaseUrl: string
    HydrusClientAPIAccessKey: string
}

type FileIdsResponse = {
    file_ids: int list
    version: int
    hydrus_version: int
}

type FilePathResponse = {
    path: string
    filetype: string
    size: int
    version: int
    hydrus_version: int
}

let getJsonAsync<'T> (httpClient: HttpClient) (url: string) : Task<'T option> =
    task {
        try
            let! response = httpClient.GetAsync(url)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                return Some(JsonSerializer.Deserialize<'T>(content))
            else
                return None
        with
        | ex ->
            printfn "Error fetching JSON from %s: %s" url ex.Message
            return None
    }

let downloadFileAsync (httpClient: HttpClient) (url: string) : Task<byte[]> =
    task {
        try
            let! response = httpClient.GetAsync(url)
            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsByteArrayAsync()
                return content
            else
                printfn "Failed to download file from %s with status code %i" url (int response.StatusCode)
                return Array.empty
        with
        | ex ->
            printfn "Error downloading file from %s: %s" url ex.Message
            return Array.empty
    }

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

let handler appSettings tags: Task =
    let tagsJson = JsonSerializer.Serialize(tags)
    let encodedTags = Uri.EscapeDataString(tagsJson)
    let baseUrl = appSettings.BaseUrl
    let getFilesUrl = $"{baseUrl}search_files?tags={encodedTags}"

    let httpClient = new HttpClient()
    httpClient.DefaultRequestHeaders.Add("Hydrus-Client-API-Access-Key", appSettings.HydrusClientAPIAccessKey)

    task {
        let! fileIdsResponse = getJsonAsync<FileIdsResponse> httpClient getFilesUrl
        match fileIdsResponse with
        | Some response ->
            for fileId in response.file_ids do
                let filePathUrl = $"{baseUrl}file_path?file_id={fileId}"
                let! filePathResponse = getJsonAsync<FilePathResponse> httpClient filePathUrl

                match filePathResponse with
                | Some pathResponse ->
                    printfn "Path for file_id %i: %s" fileId pathResponse.path
                | None ->
                    let fileUrl = $"{baseUrl}file?file_id={fileId}"
                    let! fileBytes = downloadFileAsync httpClient fileUrl
                    if fileBytes.Length > 0 then
                        printfn "File downloaded for file_id %i. Size: %i bytes" fileId fileBytes.Length
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
    |> addGlobalArgument argument1
    |> setGlobalHandler handler1 argument1
    |> invoke argv
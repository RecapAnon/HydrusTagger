open System
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks

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

[<EntryPoint>]
let main argv =
    let tags = if argv.Length > 0 then Array.toList argv else []
    if List.isEmpty tags then
        printfn "No tags provided."
        1
    else
        let tagsJson = JsonSerializer.Serialize(tags)
        let encodedTags = Uri.EscapeDataString(tagsJson)
        let baseUrl = "http://localhost:45869/get_files/"
        let getFilesUrl = $"{baseUrl}search_files?tags={encodedTags}"

        let httpClient = new HttpClient()
        httpClient.DefaultRequestHeaders.Add("Hydrus-Client-API-Access-Key", "")

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
        } |> Async.AwaitTask |> Async.RunSynchronously

        0

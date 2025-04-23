module HydrusApi

open System
open System.Net.Http
open System.Text
open System.Text.Json

type FileIdsResponse =
    { file_ids: int list
      version: int
      hydrus_version: int }

type FilePathResponse =
    { path: string
      filetype: string
      size: int
      version: int
      hydrus_version: int }

type AddTagsRequest =
    { file_id: int
      service_keys_to_tags: Map<string, string list> }

type HydrusApiClient(baseUrl: string, accessKey: string) =
    let httpClient = new HttpClient()

    do
        httpClient.BaseAddress <- Uri(baseUrl)
        httpClient.DefaultRequestHeaders.Add("Hydrus-Client-API-Access-Key", accessKey)

    member _.GetFiles(tags: string list) =
        task {
            let tagsJson = JsonSerializer.Serialize(tags)
            let encodedTags = Uri.EscapeDataString(tagsJson)
            let url = $"/get_files/search_files?tags={encodedTags}"

            let! response = httpClient.GetAsync(url)

            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                return Some(JsonSerializer.Deserialize<FileIdsResponse>(content))
            else
                return None
        }

    member _.GetFilePath(fileId: int) =
        task {
            let url = $"/get_files/file_path?file_id={fileId}"
            let! response = httpClient.GetAsync(url)

            if response.IsSuccessStatusCode then
                let! content = response.Content.ReadAsStringAsync()
                return Some(JsonSerializer.Deserialize<FilePathResponse>(content))
            else
                return None
        }

    member _.DownloadFile(fileId: int) =
        task {
            let url = $"/get_files/file?file_id={fileId}"
            let! response = httpClient.GetAsync(url)

            if response.IsSuccessStatusCode then
                return! response.Content.ReadAsByteArrayAsync()
            else
                return Array.empty
        }

    member _.AddTags(request: AddTagsRequest) =
        task {
            let url = "/add_tags/add_tags"
            let json = JsonSerializer.Serialize(request)
            let content = new StringContent(json, Encoding.UTF8, "application/json")
            let! response = httpClient.PostAsync(url, content)

            if response.IsSuccessStatusCode then
                printfn "Tags added successfully for file ID: %d" request.file_id
            else
                printfn "Failed to add tags for file ID: %d. Status code: %d" request.file_id (int response.StatusCode)
        }

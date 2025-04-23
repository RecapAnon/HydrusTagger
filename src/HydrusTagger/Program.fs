open HydrusApi
open Microsoft.Extensions.Configuration
open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open System
open System.CommandLine
open System.IO
open System.Linq
open System.Reflection
open System.Text.Json
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

let imageToOnnx (imageBytes: byte array) (size: int) =
    use stream = new MemoryStream(imageBytes)
    use image = Image.Load<Rgb24>(stream)

    let h, w = image.Height, image.Width

    let h', w' =
        if h > w then
            (size, int (float size * float w / float h))
        else
            (int (float size * float h / float w), size)

    image.Mutate(fun c ->
        c.Resize(w', h', KnownResamplers.Lanczos3) |> ignore
        c.Pad(size, size, Color.White) |> ignore)

    let width = image.Width
    let height = image.Height

    for y in 0 .. (height - h') / 2 do
        for x in 0 .. width - 1 do
            image[x, y] <- image[x, (height - h') / 2 + 1]

    for y in height - (height - h') / 2 .. height - 1 do
        for x in 0 .. width - 1 do
            image[x, y] <- image[x, height - (height - h') / 2 - 1]

    for y in 0 .. height - 1 do
        for x in 0 .. (width - w') / 2 do
            image[x, y] <- image[(width - w') / 2 + 1, y]

    for y in 0 .. height - 1 do
        for x in width - (width - w') / 2 .. width - 1 do
            image[x, y] <- image[width - (width - w') / 2 - 1, y]

    let tensor = new DenseTensor<float32>([| 1; size; size; 3 |])

    for y = 0 to size - 1 do
        for x = 0 to size - 1 do
            let pixel = image[x, y]
            tensor[0, y, x, 0] <- float32 pixel.R / 255.0f
            tensor[0, y, x, 1] <- float32 pixel.G / 255.0f
            tensor[0, y, x, 2] <- float32 pixel.B / 255.0f

    tensor

let identify (session: InferenceSession) (imageBytes: byte array) =
    let tensor = imageToOnnx imageBytes 512
    let inputs = [ NamedOnnxValue.CreateFromTensor("inputs", tensor) ]
    let results = session.Run(inputs)
    let outputTensor = results[0].AsTensor<float32>()
    let probs = outputTensor.ToArray()

    let tags =
        session.ModelMetadata.CustomMetadataMap["tags"]
        |> JsonSerializer.Deserialize<string[]>

    Array.zip tags probs
    |> Array.filter (fun (_, score) -> score >= 0.5f)
    |> Array.map fst
    |> Array.toList

let handler appSettings (tags: string[]) : Task =
    let hydrusClient =
        HydrusApiClient(appSettings.BaseUrl, appSettings.HydrusClientAPIAccessKey)

    let session = new InferenceSession(appSettings.ResnetModelPath)

    task {
        let! fileIdsResponse = hydrusClient.GetFiles(List.ofArray tags)

        match fileIdsResponse with
        | Some response ->
            for fileId in response.file_ids do
                let! filePathResponse = hydrusClient.GetFilePath(fileId)

                match filePathResponse with
                | Some pathResponse ->
                    printfn "Path for file_id %i: %s" fileId pathResponse.path
                    let newTags = identify session (File.ReadAllBytes pathResponse.path)
                    printfn "Tags: %A" newTags

                    let request =
                        { HydrusApi.AddTagsRequest.file_id = fileId
                          service_keys_to_tags = Map.ofList [ (appSettings.ServiceKey, newTags) ] }

                    do! hydrusClient.AddTags(request)
                | None ->
                    let! fileBytes = hydrusClient.DownloadFile(fileId)

                    if not (Array.isEmpty fileBytes) then
                        printfn "File downloaded for file_id %i. Size: %i bytes" fileId fileBytes.Length
                        let newTags = identify session fileBytes
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

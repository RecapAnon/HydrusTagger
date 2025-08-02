namespace HydrusTagger

open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open System.IO
open System.Linq
open System.Text.Json

type DeepdanbooruTagger(session: InferenceSession, tags: string[]) =
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

    member _.Identify(imageBytes: byte array) =
        let tensor = imageToOnnx imageBytes 512
        let inputs = [ NamedOnnxValue.CreateFromTensor("inputs", tensor) ]
        let results = session.Run(inputs)
        let outputTensor = results[0].AsTensor<float32>()
        let probs = outputTensor.ToArray()

        Array.zip tags probs
        |> Array.filter (fun (_, score) -> score >= 0.5f)
        |> Array.map fst

    interface System.IDisposable with
        member _.Dispose() = session.Dispose()

    static member Create(modelPath: string) : Result<DeepdanbooruTagger, string> =
        try
            let session = new InferenceSession(modelPath)

            try
                let tagsJson =
                    match session.ModelMetadata.CustomMetadataMap.TryGetValue("tags") with
                    | true, value -> value
                    | false, _ -> failwith "Missing 'tags' metadata in ONNX model."

                let tags = JsonSerializer.Deserialize<string[]>(tagsJson)
                Ok(new DeepdanbooruTagger(session, tags))
            with ex ->
                session.Dispose()
                Error($"Failed to load tags from model metadata: {ex.Message}")
        with ex ->
            Error($"Failed to load ONNX model from path '{modelPath}': {ex.Message}")

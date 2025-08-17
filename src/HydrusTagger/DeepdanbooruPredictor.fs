namespace HydrusTagger

open Microsoft.ML.OnnxRuntime
open Microsoft.ML.OnnxRuntime.Tensors
open SixLabors.ImageSharp
open SixLabors.ImageSharp.PixelFormats
open SixLabors.ImageSharp.Processing
open System.IO
open System.Linq

type DeepdanbooruPredictor(modelPath: string, labelPath: string, characterLabelPath: string, useCuda: bool) =
    let buildInferenceSession path =
        use sessionOptions = new SessionOptions()

        if useCuda then
            try
                sessionOptions.AppendExecutionProvider_CUDA()
            with ex ->
                sessionOptions.AppendExecutionProvider_CPU()
        else
            sessionOptions.AppendExecutionProvider_CPU()

        new InferenceSession(modelPath, sessionOptions)

    let session = buildInferenceSession modelPath
    let generalTags = File.ReadAllLines(labelPath)

    let characterTags =
        File.ReadAllLines(characterLabelPath)
        |> Array.map (fun tag -> $"character:{tag}")

    let ratingTags = [| "rating:safe"; "rating:questionable"; "rating:explicit" |]

    let tags = Array.concat [| generalTags; characterTags; ratingTags |]
    let modelTargetSize = session.InputMetadata.First().Value.Dimensions[1]

    let prepareImage (imageBytes: byte array) =
        use stream = new MemoryStream(imageBytes)
        use image = Image.Load<Rgb24>(stream)

        let h, w = image.Height, image.Width

        let h', w' =
            if h > w then
                (modelTargetSize, int (float modelTargetSize * float w / float h))
            else
                (int (float modelTargetSize * float h / float w), modelTargetSize)

        image.Mutate(fun c ->
            c.Resize(w', h', KnownResamplers.Lanczos3) |> ignore
            c.Pad(modelTargetSize, modelTargetSize, Color.White) |> ignore)

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

        let tensor = new DenseTensor<float32>([| 1; modelTargetSize; modelTargetSize; 3 |])

        for y = 0 to modelTargetSize - 1 do
            for x = 0 to modelTargetSize - 1 do
                let pixel = image[x, y]
                tensor[0, y, x, 0] <- float32 pixel.R / 255.0f
                tensor[0, y, x, 1] <- float32 pixel.G / 255.0f
                tensor[0, y, x, 2] <- float32 pixel.B / 255.0f

        tensor

    member _.predict(imageBytes: byte array) =
        let tensor = prepareImage imageBytes
        let inputName = session.InputMetadata |> Seq.head |> (fun x -> x.Key)
        let inputs = [ NamedOnnxValue.CreateFromTensor(inputName, tensor) ]
        let results = session.Run(inputs)
        let outputTensor = results[0].AsTensor<float32>()
        let probs = outputTensor.ToArray()

        Array.zip tags probs
        |> Array.filter (fun (_, score) -> score >= 0.5f)
        |> Array.map fst

    interface System.IDisposable with
        member _.Dispose() = session.Dispose()

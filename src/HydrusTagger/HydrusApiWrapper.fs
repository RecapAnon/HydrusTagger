namespace HydrusTagger

module HydrusApiWrapper =
    open Microsoft.Extensions.Logging
    open System.Threading.Tasks
    open HydrusAPI.NET.Client
    open FsToolkit.ErrorHandling

    let tryCallApi<'T when 'T :> IApiResponse>
        (logger: ILogger)
        (operationName: string)
        (apiCall: unit -> Task<'T>)
        : Async<Result<'T, string>> =
        async {
            try
                let! response = apiCall () |> Async.AwaitTask

                if response.IsSuccessStatusCode then
                    return Ok response
                else
                    logger.LogWarning(
                        "{OperationName} returned failed status: {StatusCode}",
                        operationName,
                        response.StatusCode
                    )

                    return Error $"HTTP error: %s{operationName} - Status: %b{response.IsSuccessStatusCode}"
            with ex ->
                logger.LogError(ex, "{OperationName} failed due to exception", operationName)
                return Error $"Exception during %s{operationName}: %s{ex.Message}"
        }

    let getApiResponseData (result: Result<#IOk<_>, string>) : Result<_, string> =
        result
        |> Result.map (fun resp -> resp.Ok())
        |> Result.bind (fun resp -> Result.requireNotNull "null" resp)

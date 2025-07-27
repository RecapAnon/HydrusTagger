namespace HydrusTagger

module HydrusApiWrapper =
    open Microsoft.Extensions.Logging
    open System.Threading.Tasks
    open HydrusAPI.NET.Client
    open FsToolkit.ErrorHandling

    let tryCall<'T> (logger: ILogger) (operationName: string) (request: unit -> Task<'T>) : Task<Result<'T, string>> =
        task {
            try
                let! result = request ()
                return Ok result
            with ex ->
                logger.LogError(ex, "{OperationName} failed due to exception", operationName)
                return Error $"Exception during %s{operationName}: %s{ex.Message}"
        }

    let tryCallHydrusApi<'T when 'T :> IApiResponse>
        (logger: ILogger)
        (operationName: string)
        (apiCall: unit -> Task<'T>)
        : Task<Result<'T, string>> =
        task {
            let! result = tryCall logger operationName apiCall

            return
                result
                |> Result.bind (fun response ->
                    if response.IsSuccessStatusCode then
                        Ok response
                    else
                        logger.LogWarning(
                            "{OperationName} returned failed status: {StatusCode}",
                            operationName,
                            response.StatusCode
                        )

                        Error $"HTTP error: %s{operationName} - Status: %b{response.IsSuccessStatusCode}")
        }

    let getOk (result: #IOk<'A>) : Result<'A, string> =
        result.Ok() |> Result.requireNotNull "null"

    let getApiResponseData (result: Result<#IOk<_>, string>) : Result<_, string> =
        result
        |> Result.map (fun resp -> resp.Ok())
        |> Result.bind (fun resp -> Result.requireNotNull "null" resp)

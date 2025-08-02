#nowarn 3261
namespace HydrusTagger

module HydrusApiWrapper =
    open FsToolkit.ErrorHandling
    open HydrusAPI.NET.Client
    open Microsoft.Extensions.Logging
    open ResultExtensions
    open System.Threading.Tasks

    let tryCall
        (logger: ILogger)
        (operationName: string)
        (request: unit -> Task<'T | null>)
        : Task<Result<'T, string>> =
        task {
            try
                let! result = request ()
                return Result.requireNotNull "null" result
            with ex ->
                logger.LogError(ex, "{OperationName} failed due to exception", operationName)
                return Error $"Exception during %s{operationName}: %s{ex.Message}"
        }

    let tryCallHydrusApi
        (logger: ILogger)
        (operationName: string)
        (apiCall: unit -> Task<#IApiResponse | null>)
        : Task<Result<#IApiResponse, string>> =
        taskResult {
            let! response = tryCall logger operationName apiCall

            return!
                if response.IsSuccessStatusCode then
                    Result.requireNotNull "null" response
                else
                    logger.LogWarning(
                        "{OperationName} returned failed status: {StatusCode}",
                        operationName,
                        response.StatusCode
                    )

                    Error $"HTTP error: %s{operationName} - Status: %b{response.IsSuccessStatusCode}"
        }

    let getOk (result: #IOk<'A | null>) : Result<'A, string> =
        Result.requireNotNull "null" (result.Ok())

namespace HydrusTagger

open HydrusAPI.NET
open HydrusAPI.NET.Client
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks

type CustomTokenProvider<'TTokenBase when 'TTokenBase :> TokenBase>(container: TokenContainer<'TTokenBase>) =
    inherit TokenProvider<'TTokenBase>(container.Tokens)

    let availableTokens = Dictionary<string, 'TTokenBase>()

    do
        if
            container.GetType().IsGenericType
            && container.GetType().GetGenericTypeDefinition() = typedefof<TokenContainer<ApiKeyToken>>
        then
            let apiKeyContainer = container :> obj :?> TokenContainer<ApiKeyToken>

            for token in apiKeyContainer.Tokens do
                let header = ClientUtils.ApiKeyHeaderToString(token.Header)
                let baseToken = token :> TokenBase
                let castedToken = unbox<'TTokenBase> (box baseToken)
                availableTokens.Add(header, castedToken)

    override _.GetAsync(header: string, cancellation: CancellationToken) : ValueTask<'TTokenBase> =
        ValueTask.FromResult(availableTokens[header])

namespace HydrusTagger

open Microsoft.SemanticKernel.Connectors.OpenAI
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

type TaggingService =
    { Name: string
      Endpoint: string
      Key: string
      Model: string
      SystemPrompt: string
      UserPrompt: string
      ExecutionSettings: OpenAIPromptExecutionSettings }

type LogLevelConfig = { Default: LogLevel }

type LoggingConfig = { LogLevel: LogLevelConfig }

[<CLIMutable>]
type AppSettings =
    { BaseUrl: string | null
      HydrusClientAPIAccessKey: string | null
      ServiceKey: string | null
      ResnetModelPath: string | null
      ResnetLabelPath: string | null
      WaifuModelPath: string | null
      WaifuLabelPath: string | null
      Logging: LoggingConfig | null
      Services: TaggingService array | null }

type IAppConfig =
    abstract BaseUrl: string option
    abstract HydrusClientAPIAccessKey: string option
    abstract ServiceKey: string option
    abstract ResnetModelPath: string option
    abstract ResnetLabelPath: string option
    abstract WaifuModelPath: string option
    abstract WaifuLabelPath: string option
    abstract Logging: LoggingConfig option
    abstract Services: TaggingService array option

type AppConfig(private options: IOptions<AppSettings>) =
    interface IAppConfig with
        member this.BaseUrl = Option.ofObj options.Value.BaseUrl

        member this.HydrusClientAPIAccessKey =
            Option.ofObj options.Value.HydrusClientAPIAccessKey

        member this.ServiceKey = Option.ofObj options.Value.ServiceKey
        member this.ResnetModelPath = Option.ofObj options.Value.ResnetModelPath
        member this.ResnetLabelPath = Option.ofObj options.Value.ResnetLabelPath
        member this.WaifuModelPath = Option.ofObj options.Value.WaifuModelPath
        member this.WaifuLabelPath = Option.ofObj options.Value.WaifuLabelPath
        member this.Logging = Option.ofObj options.Value.Logging
        member this.Services = Option.ofObj options.Value.Services

namespace HydrusTagger

open Microsoft.SemanticKernel.Connectors.OpenAI
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Options

[<CLIMutable>]
type TaggingService =
    { Name: string
      Endpoint: string
      Key: string
      Model: string
      SystemPrompt: string
      UserPrompt: string
      ExecutionSettings: OpenAIPromptExecutionSettings }

[<CLIMutable>]
type LogLevelConfig = { Default: LogLevel }

[<CLIMutable>]
type LoggingConfig = { LogLevel: LogLevelConfig }

[<CLIMutable>]
type AppSettings =
    { BaseUrl: string | null
      HydrusClientAPIAccessKey: string | null
      ServiceKey: string | null
      DDModelPath: string | null
      DDLabelPath: string | null
      WDModelPath: string | null
      WDLabelPath: string | null
      Logging: LoggingConfig | null
      Services: TaggingService array | null }

type IAppConfig =
    abstract BaseUrl: string option
    abstract HydrusClientAPIAccessKey: string option
    abstract ServiceKey: string option
    abstract DDModelPath: string option
    abstract DDLabelPath: string option
    abstract WDModelPath: string option
    abstract WDLabelPath: string option
    abstract Logging: LoggingConfig option
    abstract Services: TaggingService array option

type AppConfig(private settings: AppSettings) =
    interface IAppConfig with
        member _.BaseUrl = Option.ofObj settings.BaseUrl
        member _.HydrusClientAPIAccessKey = Option.ofObj settings.HydrusClientAPIAccessKey
        member _.ServiceKey = Option.ofObj settings.ServiceKey
        member _.DDModelPath = Option.ofObj settings.DDModelPath
        member _.DDLabelPath = Option.ofObj settings.DDLabelPath
        member _.WDModelPath = Option.ofObj settings.WDModelPath
        member _.WDLabelPath = Option.ofObj settings.WDLabelPath
        member _.Logging = Option.ofObj settings.Logging
        member _.Services = Option.ofObj settings.Services

    new(options: IOptions<AppSettings>) = AppConfig(options.Value)

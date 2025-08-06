// File: src/HydrusTagger/AppConfig.fs

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

type AppConfig(private settings: AppSettings) =
    interface IAppConfig with
        member _.BaseUrl = Option.ofObj settings.BaseUrl
        member _.HydrusClientAPIAccessKey = Option.ofObj settings.HydrusClientAPIAccessKey
        member _.ServiceKey = Option.ofObj settings.ServiceKey
        member _.ResnetModelPath = Option.ofObj settings.ResnetModelPath
        member _.ResnetLabelPath = Option.ofObj settings.ResnetLabelPath
        member _.WaifuModelPath = Option.ofObj settings.WaifuModelPath
        member _.WaifuLabelPath = Option.ofObj settings.WaifuLabelPath
        member _.Logging = Option.ofObj settings.Logging
        member _.Services = Option.ofObj settings.Services

    new(options: IOptions<AppSettings>) = AppConfig(options.Value)

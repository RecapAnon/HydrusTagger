namespace HydrusAPI.NET.Api
{
    public sealed partial class GetFilesApi : IGetFilesApi
    {
        public partial class GetFilesFileApiResponse
        {
            partial void OnCreated(System.Net.Http.HttpRequestMessage httpRequestMessage, System.Net.Http.HttpResponseMessage httpResponseMessage)
            {
                ContentBytes = httpResponseMessage.Content.ReadAsByteArrayAsync().Result;
                ContentHeaders = httpResponseMessage.Content.Headers;
            }

            public byte[] ContentBytes { get; set; }
            public System.Net.Http.Headers.HttpContentHeaders ContentHeaders { get; set; }
        }
    }
}

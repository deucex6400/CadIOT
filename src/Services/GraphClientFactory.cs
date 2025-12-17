using Microsoft.Graph;

namespace cad_dispatch.Services
{
    public class GraphClientFactory
    {
        public GraphServiceClient Client { get; }
        public GraphClientFactory(GraphServiceClient client)
        {
            Client = client;
        }
    }
}

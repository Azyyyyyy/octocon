using Interfold.IntegrationTests.Models;

namespace Interfold.IntegrationTests.Attributes
{
    public class ApiIntegrationAttribute() : SkipAttribute("API integration tests are gated on OCTOCON_RUN_API_INTEGRATION=true")
    {
        public override Task<bool> ShouldSkip(TestRegisteredContext context)
        {
            return Task.FromResult(!IntegrationTestEnvironment.ShouldRunApiIntegration);
        }
    }
}
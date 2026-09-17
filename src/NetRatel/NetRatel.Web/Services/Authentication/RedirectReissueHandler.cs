using System.Net;
using System.Net.Http;

namespace NetRatel.Web.Services.Authentication;

public class RedirectReissueHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await base.SendAsync(request, cancellationToken);

            if ((int)response.StatusCode is 307 or 308 && response.Headers.Location is Uri location)
            {
                response.Dispose();

                var newRequest = new HttpRequestMessage(request.Method, location);

                if (request.Content is not null)
                {
                    var buffer = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                    var newContent = new ByteArrayContent(buffer);

                    foreach (var header in request.Content.Headers)
                    {
                        newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }

                    newRequest.Content = newContent;
                }

                foreach (var header in request.Headers)
                {
                    if (string.Equals(header.Key, "Authorization", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    newRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                return await base.SendAsync(newRequest, cancellationToken);
            }

            return response;
        }
        catch (ReauthRequiredException)
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }
        catch (TaskCanceledException)
        {
            throw;
        }
    }
}

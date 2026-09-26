// A7 — Java's java.net.http.HttpClient as a foreign client of the demo host.
//
// Single-file source program, so there is no build step:
//
//     java Client.java http://127.0.0.1:8080
//
// The JDK client is the strictest of the four peers about a few things and the
// least forthcoming about others - it does not expose the trailer section at
// all - so where a check cannot be made it says SKIP with a reason rather than
// passing quietly.

import java.io.ByteArrayInputStream;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.zip.GZIPInputStream;

public class Client {

    static int failed = 0;

    static void pass(String label) {
        System.out.println("PASS\t" + label);
    }

    static void fail(String label, String detail) {
        failed++;
        System.out.println("FAIL\t" + label + "\t" + detail);
    }

    static void skip(String label, String reason) {
        System.out.println("SKIP\t" + label + "\t" + reason);
    }

    static void check(String label, boolean ok, String detail) {
        if (ok) pass(label); else fail(label, detail);
    }

    public static void main(String[] args) throws Exception {

        if (args.length < 1) {
            System.err.println("usage: Client.java <base-url>");
            System.exit(2);
        }

        var base = args[0].replaceAll("/+$", "");

        var client = HttpClient.newBuilder()
                               .followRedirects(HttpClient.Redirect.NORMAL)
                               .connectTimeout(Duration.ofSeconds(10))
                               .build();

        // ------------------------------------------------------- baseline

        var root = get(client, base + "/", null);
        check("baseline", root.statusCode() == 200 && !root.body().isEmpty(),
              "status " + root.statusCode() + ", " + root.body().length() + " chars");

        // -------------------------------------------------------- chunked

        var chunked = get(client, base + "/chunked", null);
        check("chunked",
              chunked.statusCode() == 200 && chunked.body().equals("chunk-one\nchunk-two\nchunk-three\n"),
              "status " + chunked.statusCode() + ", body " + quote(chunked.body()));

        // ------------------------------------------------------- trailers
        //
        // The body still has to arrive correctly, which is the half this can
        // check; java.net.http discards the trailer section without exposing
        // it, so the fields themselves are Go's and Node's business here.

        var trailers = get(client, base + "/trailers", null);
        check("trailers-body",
              trailers.statusCode() == 200 && trailers.body().equals("body with trailers\n"),
              "status " + trailers.statusCode() + ", body " + quote(trailers.body()));
        skip("trailers", "java.net.http does not expose the trailer section");

        // ----------------------------------------------------------- gzip

        var identity = get(client, base + "/prose", null);
        var encoded  = getBytes(client, base + "/prose", "Accept-Encoding", "gzip");

        var contentEncoding = encoded.headers().firstValue("Content-Encoding").orElse("");
        var vary            = encoded.headers().firstValue("Vary").orElse("");

        if (!contentEncoding.equals("gzip"))
            fail("gzip", "Content-Encoding " + quote(contentEncoding));

        else if (!vary.toLowerCase().contains("accept-encoding"))
            fail("gzip", "Vary " + quote(vary));

        else {
            var decoded = new String(new GZIPInputStream(new ByteArrayInputStream(encoded.body())).readAllBytes(),
                                     StandardCharsets.UTF_8);
            check("gzip", decoded.equals(identity.body()),
                  decoded.length() + " decoded vs " + identity.body().length() + " identity chars");
        }

        // ----------------------------------------------- HEAD matches GET

        var getFile  = get(client, base + "/files/resource.txt", null);
        var headFile = client.send(HttpRequest.newBuilder(URI.create(base + "/files/resource.txt"))
                                              .method("HEAD", HttpRequest.BodyPublishers.noBody())
                                              .timeout(Duration.ofSeconds(20))
                                              .build(),
                                   HttpResponse.BodyHandlers.ofString());

        var headLength = headFile.headers().firstValueAsLong("Content-Length").orElse(-1);

        check("head-matches-get",
              headFile.statusCode() == 200 &&
              headFile.body().isEmpty() &&
              headLength == getFile.body().getBytes(StandardCharsets.UTF_8).length &&
              headFile.headers().firstValue("ETag").equals(getFile.headers().firstValue("ETag")),
              "status " + headFile.statusCode() + ", " + headFile.body().length() + " body chars, " +
              "Content-Length " + headLength + " vs GET " + getFile.body().length());

        // ---------------------------------------------------------- range

        var ranged = get(client, base + "/files/resource.txt", "Range", "bytes=0-9");
        var contentRange = ranged.headers().firstValue("Content-Range").orElse("");

        check("range",
              ranged.statusCode() == 206 &&
              ranged.body().length() == 10 &&
              contentRange.startsWith("bytes 0-9/"),
              "status " + ranged.statusCode() + ", " + ranged.body().length() + " chars, Content-Range " + quote(contentRange));

        // -------------------------------------------------- accept-ranges

        check("accept-ranges",
              getFile.headers().firstValue("Accept-Ranges").orElse("").equals("bytes"),
              "Accept-Ranges " + quote(getFile.headers().firstValue("Accept-Ranges").orElse("")));

        // ----------------------------------------------------- status 404

        var notFound = get(client, base + "/status/404", null);
        check("status-404", notFound.statusCode() == 404, "status " + notFound.statusCode());

        // ------------------------------------------------------- redirect

        var redirected = get(client, base + "/redirect/302", null);
        check("redirect", redirected.statusCode() == 200, "final status " + redirected.statusCode());

        // ---------------------------------------------------------- reuse

        var again1 = get(client, base + "/", null);
        var again2 = get(client, base + "/", null);
        check("reuse", again1.statusCode() == 200 && again2.statusCode() == 200,
              again1.statusCode() + " / " + again2.statusCode());

        System.exit(failed > 0 ? 1 : 0);

    }

    static HttpResponse<String> get(HttpClient client, String url, String header, String... value) throws Exception {
        var builder = HttpRequest.newBuilder(URI.create(url)).timeout(Duration.ofSeconds(20)).GET();
        if (header != null) builder.header(header, value[0]);
        return client.send(builder.build(), HttpResponse.BodyHandlers.ofString());
    }

    static HttpResponse<byte[]> getBytes(HttpClient client, String url, String header, String value) throws Exception {
        var request = HttpRequest.newBuilder(URI.create(url))
                                 .timeout(Duration.ofSeconds(20))
                                 .header(header, value)
                                 .GET()
                                 .build();
        return client.send(request, HttpResponse.BodyHandlers.ofByteArray());
    }

    static String quote(String text) {
        return "\"" + (text.length() > 80 ? text.substring(0, 80) + "…" : text).replace("\n", "\\n") + "\"";
    }

}

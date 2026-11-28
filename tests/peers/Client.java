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
import java.net.ProtocolException;
import java.net.URI;
import java.net.http.HttpClient;
import java.net.http.HttpRequest;
import java.net.http.HttpResponse;
import java.net.http.WebSocket;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.time.Duration;
import java.util.Arrays;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.CompletionStage;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
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
        //
        // A known JDK deviation, pinned rather than passed, and only on the
        // JDKs that have it. The October 2026 updates - 17.0.21, 21.0.13 and
        // 25.0.5, as apache/druid#20717 lists them - carry JDK-8383439,
        // "Enhance HTTP Client Response Handling", after which java.net.http
        // wants a CRLF straight after the last chunk and fails any response
        // with a trailer section with ProtocolException "CR expected". RFC
        // 9112 Section 7.1 puts the
        // trailer section in that very place, and the demo's is well formed.
        // Measured on 2026-11-01: 17.0.20.1, 25.0.1 and 25.0.4.1 read the
        // body, 21.0.13 and 25.0.5 throw. JDK-8383439 is not public, and no
        // public report of the regression was found. Until the nightly's JDK
        // took the update on 2026-10-29 this threw nowhere here, and the
        // exception then ended the program after two checks without one FAIL
        // - which is why tests/interop.sh now counts a peer that dies.
        //
        // Exactly that exception passes under its own name; any other is a
        // failure, and so is a wrong body from a JDK that reads it.

        try {
            var trailers = get(client, base + "/trailers", null);
            check("trailers-body",
                  trailers.statusCode() == 200 && trailers.body().equals("body with trailers\n"),
                  "status " + trailers.statusCode() + ", body " + quote(trailers.body()));
        }
        catch (ProtocolException e) {
            if ("CR expected".equals(e.getMessage()))
                pass("trailers-body-rejected (known JDK deviation, JDK-8383439)");
            else
                fail("trailers-body", e.toString());
        }
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

        // ---------------------------------------------------- early hints
        //
        // /early-hints sends a 103 with a preload Link and then the 200.
        // java.net.http reads past a 1xx without exposing it, so this checks
        // the half it can: the final response, and the request after it,
        // which has to get its own response. RFC 8297's security
        // considerations are about a client that takes the 103 for the final
        // response and misreads everything after it on the connection.

        var hinted = get(client, base + "/early-hints", null);
        var after  = get(client, base + "/", null);
        check("early-hints",
              hinted.statusCode() == 200 && hinted.body().equals("a 103 went first\n") &&
              after.body().equals("Hermod HTTP/1.1 demo host\n"),
              "status " + hinted.statusCode() + ", body " + quote(hinted.body()) + ", then " + quote(after.body()));

        // ------------------------------------------------------ WebSocket
        //
        // java.net.http.WebSocket, the JDK's own client, against /ws on the
        // HTTP port: the RFC 9110 §7.8 Upgrade path. It offers no extension,
        // so this is the uncompressed path, and it hands a message over in
        // parts with a last flag, which the listener below puts together.

        webSocketChecks(client, base);

        System.exit(failed > 0 ? 1 : 0);

    }

    static final class Echo implements WebSocket.Listener {

        final LinkedBlockingQueue<Object> messages = new LinkedBlockingQueue<>();
        final CompletableFuture<Integer>  closed   = new CompletableFuture<>();
        final StringBuilder               text     = new StringBuilder();
        final java.io.ByteArrayOutputStream binary = new java.io.ByteArrayOutputStream();

        public CompletionStage<?> onText(WebSocket socket, CharSequence data, boolean last) {
            text.append(data);
            if (last) { messages.add(text.toString()); text.setLength(0); }
            socket.request(1);
            return null;
        }

        public CompletionStage<?> onBinary(WebSocket socket, ByteBuffer data, boolean last) {
            var bytes = new byte[data.remaining()];
            data.get(bytes);
            binary.writeBytes(bytes);
            if (last) { messages.add(binary.toByteArray()); binary.reset(); }
            socket.request(1);
            return null;
        }

        public CompletionStage<?> onClose(WebSocket socket, int statusCode, String reason) {
            closed.complete(statusCode);
            return null;
        }

        public void onError(WebSocket socket, Throwable error) {
            closed.completeExceptionally(error);
        }

        Object next() throws InterruptedException {
            return messages.poll(20, TimeUnit.SECONDS);
        }

    }

    static void webSocketChecks(HttpClient client, String base) throws Exception {

        var echo   = new Echo();
        var socket = client.newWebSocketBuilder()
                           .subprotocols("echo")
                           .connectTimeout(Duration.ofSeconds(10))
                           .buildAsync(URI.create(base.replaceFirst("^http", "ws") + "/ws"), echo)
                           .get(20, TimeUnit.SECONDS);

        check("ws/subprotocol", "echo".equals(socket.getSubprotocol()), "negotiated " + quote(socket.getSubprotocol()));

        socket.sendText("hello over the Upgrade", true).get(20, TimeUnit.SECONDS);
        var text = echo.next();
        check("ws/echo-text", "hello over the Upgrade".equals(text), "got " + text);

        var utf8 = "Grüße — ✓ 🌍";
        socket.sendText(utf8, true).get(20, TimeUnit.SECONDS);
        var utf8Echo = echo.next();
        check("ws/echo-utf8", utf8.equals(utf8Echo), "got " + utf8Echo);

        var octets = new byte[256];
        for (var i = 0; i < octets.length; i++) octets[i] = (byte) i;
        socket.sendBinary(ByteBuffer.wrap(octets), true).get(20, TimeUnit.SECONDS);
        var binary = echo.next();
        check("ws/echo-binary", binary instanceof byte[] b && Arrays.equals(b, octets),
              "got " + (binary instanceof byte[] b ? b.length + " octets" : String.valueOf(binary)));

        // Past 65535 octets, the 64-bit payload length (RFC 6455 §5.2), and
        // sent in three fragments: a continuation frame each after the first.
        var part  = "abcdefghij".repeat(10000);
        socket.sendText(part, false).get(20, TimeUnit.SECONDS);
        socket.sendText(part, false).get(20, TimeUnit.SECONDS);
        socket.sendText(part, true ).get(20, TimeUnit.SECONDS);
        var large = echo.next();
        check("ws/echo-fragmented-large", (part + part + part).equals(large),
              "sent " + 3 * part.length() + " characters in three fragments, got " +
              (large instanceof String l ? l.length() : String.valueOf(large)));

        socket.sendClose(WebSocket.NORMAL_CLOSURE, "done").get(20, TimeUnit.SECONDS);
        Integer code;
        try { code = echo.closed.get(20, TimeUnit.SECONDS); }
        catch (Exception e) { code = null; }
        check("ws/close", code != null && code == WebSocket.NORMAL_CLOSURE, "close code " + code);

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

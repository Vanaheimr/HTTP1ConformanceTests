// A7 — Go net/http as a foreign client of the Hermod HTTP/1.x demo host.
//
// The point of this file is that nothing in it is ours. Go's HTTP stack has its
// own chunked decoder, its own header parser, its own connection pool and its
// own opinions about redirects, and it shares no lineage with .NET. Every check
// below is one our own harnesses already make — which is exactly why it is
// worth making them again from outside.
//
//	go run client.go http://127.0.0.1:8080
//
// Output is one line per check: PASS/FAIL/SKIP, tab-separated, so the driver
// can count without parsing prose.
package main

import (
	"compress/gzip"
	"fmt"
	"io"
	"net/http"
	"os"
	"strings"
	"time"
)

var failed = 0

func pass(label string) {
	fmt.Printf("PASS\t%s\n", label)
}

func fail(label, detail string) {
	failed++
	fmt.Printf("FAIL\t%s\t%s\n", label, detail)
}

func skip(label, reason string) {
	fmt.Printf("SKIP\t%s\t%s\n", label, reason)
}

func check(label string, ok bool, detail string) {
	if ok {
		pass(label)
	} else {
		fail(label, detail)
	}
}

func main() {

	if len(os.Args) < 2 {
		fmt.Fprintln(os.Stderr, "usage: client.go <base-url>")
		os.Exit(2)
	}

	base := strings.TrimRight(os.Args[1], "/")

	// One client for everything, so the connection pool is exercised rather
	// than sidestepped. The redirect policy is Go's default: follow.
	client := &http.Client{Timeout: 20 * time.Second}

	// ---------------------------------------------------------- baseline

	if resp, body, err := get(client, base+"/", nil); err != nil {
		fail("baseline", err.Error())
	} else {
		check("baseline", resp.StatusCode == 200 && len(body) > 0,
			fmt.Sprintf("status %d, %d bytes", resp.StatusCode, len(body)))
	}

	// ----------------------------------------------------------- chunked
	//
	// Go strips the chunk framing itself, so what is compared here is what its
	// decoder produced - including the chunk extensions it had to skip over.

	if resp, body, err := get(client, base+"/chunked", nil); err != nil {
		fail("chunked", err.Error())
	} else {
		want := "chunk-one\nchunk-two\nchunk-three\n"
		check("chunked", resp.StatusCode == 200 && string(body) == want,
			fmt.Sprintf("status %d, body %q", resp.StatusCode, string(body)))
	}

	// ---------------------------------------------------------- trailers
	//
	// Go is one of the few clients that exposes the trailer section at all, and
	// it only populates resp.Trailer after the body has been read to EOF.

	if resp, body, err := get(client, base+"/trailers", nil); err != nil {
		fail("trailers", err.Error())
	} else {
		checksum := resp.Trailer.Get("X-Demo-Checksum")
		check("trailers", resp.StatusCode == 200 &&
			string(body) == "body with trailers\n" &&
			checksum == "deadbeef",
			fmt.Sprintf("status %d, body %q, X-Demo-Checksum %q", resp.StatusCode, string(body), checksum))
	}

	// -------------------------------------------------------------- gzip
	//
	// Asking for the coding by hand switches off Go's transparent
	// decompression, so the bytes below are the ones that crossed the wire and
	// the decoder under test is this program's, not the transport's.

	identity, identityErr := getBody(client, base+"/prose", nil)
	resp, encoded, err := get(client, base+"/prose", map[string]string{"Accept-Encoding": "gzip"})

	switch {
	case identityErr != nil || err != nil:
		fail("gzip", fmt.Sprintf("%v / %v", identityErr, err))
	case resp.Header.Get("Content-Encoding") != "gzip":
		fail("gzip", fmt.Sprintf("Content-Encoding %q", resp.Header.Get("Content-Encoding")))
	case !strings.Contains(strings.ToLower(resp.Header.Get("Vary")), "accept-encoding"):
		fail("gzip", fmt.Sprintf("Vary %q", resp.Header.Get("Vary")))
	default:
		reader, gzErr := gzip.NewReader(strings.NewReader(string(encoded)))
		if gzErr != nil {
			fail("gzip", gzErr.Error())
			break
		}
		decoded, readErr := io.ReadAll(reader)
		check("gzip", readErr == nil && string(decoded) == string(identity),
			fmt.Sprintf("%d decoded vs %d identity bytes", len(decoded), len(identity)))
	}

	// -------------------------------------------------- HEAD matches GET
	//
	// RFC 9110, Section 9.3.2: the header section of a HEAD response is the one
	// the GET would have had. The body is what differs, and only that.

	getResp, getBodyBytes, getErr := get(client, base+"/files/resource.txt", nil)
	headResp, headBody, headErr := do(client, "HEAD", base+"/files/resource.txt", nil)

	switch {
	case getErr != nil || headErr != nil:
		fail("head-matches-get", fmt.Sprintf("%v / %v", getErr, headErr))
	default:
		check("head-matches-get",
			headResp.StatusCode == 200 &&
				len(headBody) == 0 &&
				headResp.ContentLength == int64(len(getBodyBytes)) &&
				headResp.Header.Get("ETag") == getResp.Header.Get("ETag"),
			fmt.Sprintf("status %d, %d body bytes, Content-Length %d vs GET %d, ETag %q vs %q",
				headResp.StatusCode, len(headBody), headResp.ContentLength, len(getBodyBytes),
				headResp.Header.Get("ETag"), getResp.Header.Get("ETag")))
	}

	// ------------------------------------------------------------- range

	if resp, body, err := get(client, base+"/files/resource.txt",
		map[string]string{"Range": "bytes=0-9"}); err != nil {
		fail("range", err.Error())
	} else {
		check("range", resp.StatusCode == 206 &&
			len(body) == 10 &&
			strings.HasPrefix(resp.Header.Get("Content-Range"), "bytes 0-9/"),
			fmt.Sprintf("status %d, %d bytes, Content-Range %q",
				resp.StatusCode, len(body), resp.Header.Get("Content-Range")))
	}

	// ----------------------------------------------------- accept-ranges

	if resp, _, err := get(client, base+"/files/resource.txt", nil); err != nil {
		fail("accept-ranges", err.Error())
	} else {
		check("accept-ranges", resp.Header.Get("Accept-Ranges") == "bytes",
			fmt.Sprintf("Accept-Ranges %q", resp.Header.Get("Accept-Ranges")))
	}

	// -------------------------------------------------------- status 404

	if resp, _, err := get(client, base+"/status/404", nil); err != nil {
		fail("status-404", err.Error())
	} else {
		check("status-404", resp.StatusCode == 404, fmt.Sprintf("status %d", resp.StatusCode))
	}

	// ---------------------------------------------------------- redirect

	if resp, _, err := get(client, base+"/redirect/302", nil); err != nil {
		fail("redirect", err.Error())
	} else {
		check("redirect", resp.StatusCode == 200,
			fmt.Sprintf("final status %d after %d hops", resp.StatusCode, len(resp.Request.URL.Path)))
	}

	// ------------------------------------------------------------- reuse
	//
	// Two requests through one client. Whether the connection was reused is
	// Go's business, but a second request that fails after a first that worked
	// is this server's.

	_, _, first := get(client, base+"/", nil)
	_, _, second := get(client, base+"/", nil)
	check("reuse", first == nil && second == nil, fmt.Sprintf("%v / %v", first, second))

	_ = skip

	if failed > 0 {
		os.Exit(1)
	}

}

func get(client *http.Client, url string, headers map[string]string) (*http.Response, []byte, error) {
	return do(client, "GET", url, headers)
}

func getBody(client *http.Client, url string, headers map[string]string) ([]byte, error) {
	_, body, err := do(client, "GET", url, headers)
	return body, err
}

func do(client *http.Client, method, url string, headers map[string]string) (*http.Response, []byte, error) {

	request, err := http.NewRequest(method, url, nil)
	if err != nil {
		return nil, nil, err
	}

	for name, value := range headers {
		request.Header.Set(name, value)
	}

	response, err := client.Do(request)
	if err != nil {
		return nil, nil, err
	}
	defer response.Body.Close()

	// Read to EOF even when the body is not wanted: it is what lets the
	// connection go back in the pool, and it is what populates the trailers.
	body, err := io.ReadAll(response.Body)
	if err != nil {
		return response, nil, err
	}

	return response, body, nil

}

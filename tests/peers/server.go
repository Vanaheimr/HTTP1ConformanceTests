// A7 — Go net/http as a foreign *server* for Hermod's HTTP/1.x client.
//
// This is the direction that had no independent witness at all. Our server has
// been judged by curl, by Autobahn and now by four foreign clients; our client
// had only ever talked to our own server. Everything a client can get wrong
// about framing - chunk decoding, trailer collection, content codings, reading
// a Content-Length body to exactly its length - is decided here by Go rather
// than by us.
//
//	go run server.go            # picks a free port and prints it
//	go run server.go 18080      # a fixed one
//
// Prints "LISTENING <port>" on stdout once accepting, so a driver can wait for
// a line rather than for a guess at a startup delay.
package main

import (
	"compress/gzip"
	"fmt"
	"net"
	"net/http"
	"os"
	"strings"
)

const plainBody = "hello from go\n"

func main() {

	address := "127.0.0.1:0"
	if len(os.Args) > 1 {
		address = "127.0.0.1:" + os.Args[1]
	}

	listener, err := net.Listen("tcp", address)
	if err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}

	mux := http.NewServeMux()

	// Content-Length framed: net/http sets it because the body is small enough
	// to buffer and no Flush happens.
	mux.HandleFunc("/plain", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		fmt.Fprint(w, plainBody)
	})

	// Chunked: flushing before the handler returns is what makes net/http give
	// up on Content-Length and frame it in chunks instead.
	mux.HandleFunc("/chunked", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		flusher, _ := w.(http.Flusher)
		for _, part := range []string{"one\n", "two\n", "three\n"} {
			fmt.Fprint(w, part)
			if flusher != nil {
				flusher.Flush()
			}
		}
	})

	// Trailers: declared up front, set after the body. Go writes them after the
	// terminal chunk, which is the only place they can go.
	mux.HandleFunc("/trailers", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		w.Header().Set("Trailer", "X-Peer-Checksum")
		flusher, _ := w.(http.Flusher)
		fmt.Fprint(w, "trailing body\n")
		if flusher != nil {
			flusher.Flush()
		}
		w.Header().Set("X-Peer-Checksum", "c0ffee")
	})

	// A content coding produced by somebody else's gzip.
	mux.HandleFunc("/gzip", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "text/plain")
		w.Header().Set("Content-Encoding", "gzip")
		writer := gzip.NewWriter(w)
		fmt.Fprint(writer, strings.Repeat("compressible line\n", 64))
		writer.Close()
	})

	mux.HandleFunc("/status/404", func(w http.ResponseWriter, r *http.Request) {
		http.Error(w, "not here\n", http.StatusNotFound)
	})

	port := listener.Addr().(*net.TCPAddr).Port
	fmt.Printf("LISTENING %d\n", port)
	os.Stdout.Sync()

	server := &http.Server{Handler: mux}
	if err := server.Serve(listener); err != nil {
		fmt.Fprintln(os.Stderr, err)
		os.Exit(1)
	}

}

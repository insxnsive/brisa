package main

import (
	"bytes"
	"encoding/json"
	"testing"
)

func TestArgumentGateAndExactSuccess(t *testing.T) {
	for _, args := range [][]string{nil, {"--other"}, {"--self-test", "extra"}} {
		var out, errout bytes.Buffer
		if run(args, &out, &errout) == 0 || out.Len() != 0 {
			t.Fatal("invalid arguments ran diagnostic")
		}
	}
	var out, errout bytes.Buffer
	if code := run([]string{"--self-test"}, &out, &errout); code != 0 {
		t.Fatalf("self-test exit %d", code)
	}
	const want = "{\"schemaVersion\":1,\"scope\":\"loopback-only\",\"tcp\":true,\"udp\":true,\"dns\":true,\"shutdown\":true}\n"
	if out.String() != want {
		t.Fatal("unexpected output")
	}
	var object map[string]any
	if err := json.Unmarshal(out.Bytes(), &object); err != nil || len(object) != 6 {
		t.Fatal("invalid schema")
	}
}

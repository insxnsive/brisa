package macengine

import (
	"context"
	"testing"
	"time"
)

func TestEncryptedLoopbackSelfTest(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := SelfTest(ctx); err != nil {
		t.Fatal(err)
	}
}

func TestEncryptedIPv6LoopbackSelfTest(t *testing.T) {
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()
	if err := selfTestIPv6(ctx); err != nil {
		t.Fatal(err)
	}
}

package auth

import (
	"crypto/aes"
	"crypto/cipher"
	"crypto/rand"
	"fmt"
)

const sessionKeyBytes = 32
const sessionNonceBytes = 12

func sessionAEAD(key []byte) (cipher.AEAD, error) {
	if len(key) != sessionKeyBytes {
		return nil, fmt.Errorf("invalid session encryption key")
	}
	block, err := aes.NewCipher(key)
	if err != nil {
		return nil, fmt.Errorf("session encryption unavailable")
	}
	return cipher.NewGCM(block)
}

func encryptSessionWithKey(key, plaintext []byte) ([]byte, error) {
	if len(plaintext) == 0 {
		return nil, fmt.Errorf("cannot protect an empty session")
	}
	aead, err := sessionAEAD(key)
	if err != nil {
		return nil, err
	}
	nonce := make([]byte, sessionNonceBytes)
	if _, err := rand.Read(nonce); err != nil {
		return nil, fmt.Errorf("session nonce unavailable")
	}
	return aead.Seal(nonce, nonce, plaintext, []byte(darwinSessionHeader)), nil
}

func decryptSessionWithKey(key, ciphertext []byte) ([]byte, error) {
	aead, err := sessionAEAD(key)
	if err != nil {
		return nil, err
	}
	if len(ciphertext) < sessionNonceBytes+aead.Overhead() {
		return nil, fmt.Errorf("invalid encrypted session")
	}
	plaintext, err := aead.Open(nil, ciphertext[:sessionNonceBytes], ciphertext[sessionNonceBytes:], []byte(darwinSessionHeader))
	if err != nil {
		return nil, fmt.Errorf("session authentication failed")
	}
	return plaintext, nil
}

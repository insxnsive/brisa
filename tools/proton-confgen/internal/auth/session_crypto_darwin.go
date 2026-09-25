//go:build darwin && cgo

package auth

/*
#cgo LDFLAGS: -framework CoreFoundation -framework Security
#include <CoreFoundation/CoreFoundation.h>
#include <Security/Security.h>
#include <stdlib.h>

static CFMutableDictionaryRef brisaQuery(const char *service, const char *account) {
    CFStringRef s = CFStringCreateWithCString(NULL, service, kCFStringEncodingUTF8);
    CFStringRef a = CFStringCreateWithCString(NULL, account, kCFStringEncodingUTF8);
    if (!s || !a) { if (s) CFRelease(s); if (a) CFRelease(a); return NULL; }
    CFMutableDictionaryRef q = CFDictionaryCreateMutable(NULL, 0, &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    if (q) {
		CFDictionarySetValue(q, kSecClass, kSecClassGenericPassword);
		CFDictionarySetValue(q, kSecAttrService, s);
		CFDictionarySetValue(q, kSecAttrAccount, a);
		CFDictionarySetValue(q, kSecUseDataProtectionKeychain, kCFBooleanTrue);
		CFDictionarySetValue(q, kSecUseAuthenticationUI, kSecUseAuthenticationUIFail);
    }
    CFRelease(s); CFRelease(a);
    return q;
}

static OSStatus brisaCopyKey(const char *service, const char *account, CFDataRef *out) {
    *out = NULL;
    CFMutableDictionaryRef q = brisaQuery(service, account);
    if (!q) return errSecAllocate;
    CFDictionarySetValue(q, kSecReturnData, kCFBooleanTrue);
    CFDictionarySetValue(q, kSecMatchLimit, kSecMatchLimitOne);
    OSStatus status = SecItemCopyMatching(q, (CFTypeRef *)out);
    CFRelease(q);
    return status;
}

static OSStatus brisaAddKey(const char *service, const char *account, const unsigned char *key, CFIndex length) {
    CFMutableDictionaryRef q = brisaQuery(service, account);
    if (!q) return errSecAllocate;
    CFDataRef value = CFDataCreate(NULL, key, length);
    if (!value) { CFRelease(q); return errSecAllocate; }
    CFDictionarySetValue(q, kSecValueData, value);
    CFDictionarySetValue(q, kSecAttrAccessible, kSecAttrAccessibleWhenUnlockedThisDeviceOnly);
    OSStatus status = SecItemAdd(q, NULL);
    CFRelease(value); CFRelease(q);
    return status;
}

static OSStatus brisaDeleteKey(const char *service, const char *account) {
    CFMutableDictionaryRef q = brisaQuery(service, account);
    if (!q) return errSecAllocate;
    OSStatus status = SecItemDelete(q);
    CFRelease(q);
    return status;
}
*/
import "C"

import (
	"crypto/rand"
	"fmt"
	"strings"
	"unsafe"
)

type sessionKeychainTarget struct{ service, account string }

var activeSessionKeychainTarget = sessionKeychainTarget{
	service: "dev.insxnsive.brisa.session-key.v1",
	account: "Brisa session encryption",
}

func sessionStorageUsesEncryption() bool { return true }

func keychainCopy(target sessionKeychainTarget) ([]byte, C.OSStatus) {
	service := C.CString(target.service)
	account := C.CString(target.account)
	defer C.free(unsafe.Pointer(service))
	defer C.free(unsafe.Pointer(account))
	var value C.CFDataRef
	status := C.brisaCopyKey(service, account, &value)
	if status != C.errSecSuccess {
		return nil, status
	}
	defer C.CFRelease(C.CFTypeRef(value))
	if value == nil {
		return nil, C.errSecDecode
	}
	if C.CFDataGetLength(value) != sessionKeyBytes {
		return nil, C.errSecDecode
	}
	key := C.GoBytes(unsafe.Pointer(C.CFDataGetBytePtr(value)), C.int(sessionKeyBytes))
	return key, status
}

func keychainAdd(target sessionKeychainTarget, key []byte) C.OSStatus {
	service := C.CString(target.service)
	account := C.CString(target.account)
	defer C.free(unsafe.Pointer(service))
	defer C.free(unsafe.Pointer(account))
	return C.brisaAddKey(service, account, (*C.uchar)(unsafe.Pointer(&key[0])), C.CFIndex(len(key)))
}

// Used only by native tests with their randomly generated target.
func deleteDisposableSessionKey(target sessionKeychainTarget) {
	if !strings.HasPrefix(target.service, "dev.insxnsive.brisa.test.") {
		return
	}
	service := C.CString(target.service)
	account := C.CString(target.account)
	defer C.free(unsafe.Pointer(service))
	defer C.free(unsafe.Pointer(account))
	_ = C.brisaDeleteKey(service, account)
}

func sessionKey(create bool) ([]byte, error) {
	target := activeSessionKeychainTarget
	key, status := keychainCopy(target)
	if status == C.errSecSuccess {
		return key, nil
	}
	if status != C.errSecItemNotFound {
		return nil, fmt.Errorf("macOS Keychain key unavailable")
	}
	if !create {
		return nil, fmt.Errorf("macOS Keychain key missing")
	}
	key = make([]byte, sessionKeyBytes)
	if _, err := rand.Read(key); err != nil {
		return nil, fmt.Errorf("session key generation failed")
	}
	status = keychainAdd(target, key)
	if status == C.errSecDuplicateItem {
		clear(key)
		key, status = keychainCopy(target)
		if status != C.errSecSuccess {
			return nil, fmt.Errorf("macOS Keychain key unavailable")
		}
		return key, nil
	}
	if status != C.errSecSuccess {
		clear(key)
		return nil, fmt.Errorf("macOS Keychain key unavailable")
	}
	return key, nil
}

func protectSessionBytes(payload []byte) ([]byte, error) {
	key, err := sessionKey(true)
	if err != nil {
		return nil, err
	}
	defer clear(key)
	return encryptSessionWithKey(key, payload)
}

func unprotectSessionBytes(ciphertext []byte) ([]byte, error) {
	key, err := sessionKey(false)
	if err != nil {
		return nil, err
	}
	defer clear(key)
	return decryptSessionWithKey(key, ciphertext)
}

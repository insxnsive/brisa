package macengine

import (
	"go/ast"
	"go/parser"
	"go/token"
	"path/filepath"
	"runtime"
	"testing"
)

// The packaged self-test must join even short-lived signal-sending goroutines.
// Keep this structural ownership check beside the encrypted runtime tests: a
// buffered result channel proves delivery, not completion of the producer.
func TestSelfTestRegistersEveryWorkerForJoining(t *testing.T) {
	_, source, _, ok := runtime.Caller(0)
	if !ok {
		t.Fatal("test source unavailable")
	}
	file, err := parser.ParseFile(token.NewFileSet(), filepath.Join(filepath.Dir(source), "selftest.go"), nil, 0)
	if err != nil {
		t.Fatal(err)
	}
	checked := 0
	ast.Inspect(file, func(node ast.Node) bool {
		launch, ok := node.(*ast.GoStmt)
		if !ok {
			return true
		}
		checked++
		body, ok := launch.Call.Fun.(*ast.FuncLit)
		if !ok {
			t.Error("self-test worker has no visible ownership")
			return false
		}
		joined := false
		for _, statement := range body.Body.List {
			cleanup, ok := statement.(*ast.DeferStmt)
			if !ok {
				continue
			}
			call, ok := cleanup.Call.Fun.(*ast.SelectorExpr)
			if !ok {
				continue
			}
			owner, ok := call.X.(*ast.Ident)
			if ok && owner.Name == "workers" && call.Sel.Name == "Done" {
				joined = true
			}
		}
		if !joined {
			t.Error("self-test goroutine lacks deferred worker completion")
		}
		return false
	})
	if checked == 0 {
		t.Fatal("self-test worker coverage is empty")
	}
}

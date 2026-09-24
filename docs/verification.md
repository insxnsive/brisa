# Preview verification

The first Brisa preview has passed these local checks:

- 23 packaging, release and UI contract tests.
- 19 Node backend tests and the retained Go helper test suites.
- 20 native core tests, 47 navigation assertions and 94 loaded-window appearance assertions.
- C# to Node request/response integration checks.
- A real portable upgrade from a disposable `0.0.1` build to `0.1.0-beta.1`, with rendered launches before and after, package checksum verification and preservation of a test file outside the versioned app directory.
- A build from the source ZIP without a Git checkout.
- Independent review of update shutdown ordering and the release scripts.

The packaged executable was also inspected in isolated UI mode. A manual desktop-click check was not completed. No live account, existing tunnel or network driver was changed.

Setup's command-line entry point was checked without installation. A normal install/uninstall, installed-app upgrade, real Proton verification and live tunnel routing still need acceptance on a suitable test machine. The preview is unsigned. These checks are not a claim of production readiness.

The GitHub workflow reruns the offline suites, packages the app and exercises the disposable portable upgrade before publishing a version. See the run linked to the release commit for its result.

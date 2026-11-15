const MESSAGES = Object.freeze({
  INIT_DATA_NOT_PRIVATE: 'Brisa data directory is not private.',
  INIT_DATA_MIGRATION_FAILED: 'Brisa private data migration failed safely.',
  INIT_MIGRATION_INSPECTION_UNAVAILABLE: 'WireSock migration inspection is unavailable.',
  INIT_DATA_DIR_INVALID: 'BRISA_DATA_DIR must be an absolute path.',
  INIT_UNKNOWN: 'Backend initialization failed safely.',
});

export class BackendInitializationError extends Error {
  constructor(code) {
    const safeCode = Object.hasOwn(MESSAGES, code) ? code : 'INIT_UNKNOWN';
    super(MESSAGES[safeCode]);
    this.code = safeCode;
  }
}

export function dataDirectoryFailure(error) {
  const code = Object.keys(MESSAGES).find(key => MESSAGES[key] === error?.message);
  return new BackendInitializationError(code);
}

export function initializationFailureFrame(error) {
  const safe = error instanceof BackendInitializationError ? new BackendInitializationError(error.code) : new BackendInitializationError('INIT_UNKNOWN');
  return { id: '', ok: false, result: {}, code: safe.code, error: safe.message };
}

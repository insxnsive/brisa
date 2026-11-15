import assert from 'node:assert/strict';
import test from 'node:test';
import { BackendInitializationError, dataDirectoryFailure, initializationFailureFrame } from '../src/startup-error.mjs';

for (const [message, code] of [
  ['Brisa data directory is not private.', 'INIT_DATA_NOT_PRIVATE'],
  ['Brisa private data migration failed safely.', 'INIT_DATA_MIGRATION_FAILED'],
  ['WireSock migration inspection is unavailable.', 'INIT_MIGRATION_INSPECTION_UNAVAILABLE'],
  ['BRISA_DATA_DIR must be an absolute path.', 'INIT_DATA_DIR_INVALID'],
]) test(`data-directory startup failure maps to ${code}`, () => {
  const failure = dataDirectoryFailure(new Error(message));
  assert.ok(failure instanceof BackendInitializationError);
  assert.deepEqual(initializationFailureFrame(failure), { id: '', ok: false, result: {}, code, error: message });
});

test('unknown startup exceptions never expose their message or a forged code', () => {
  for (const error of [new Error('synthetic-secret C:\\private'), { code: 'INIT_DATA_NOT_PRIVATE', message: 'synthetic-secret' }, dataDirectoryFailure(new Error('synthetic-secret')), dataDirectoryFailure(new Error('Brisa data directory is not private. C:\\synthetic-secret'))]) {
    const frame = initializationFailureFrame(error);
    assert.equal(frame.code, 'INIT_UNKNOWN');
    assert.equal(frame.error, 'Backend initialization failed safely.');
    assert.doesNotMatch(JSON.stringify(frame), /synthetic-secret|private/);
  }
});

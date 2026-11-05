import path from 'path';
import { fileURLToPath } from 'url';
import fs from 'fs';
import os from 'os';
import { app } from './native-app-shim.mjs';
import { spawn } from 'child_process';
import * as logger from './logger';
import { randomUUID } from 'crypto';
import { StringDecoder } from 'string_decoder';
import type { RouteProbeResult } from './route-proof';
import type { ProtonRouteMetadata } from './route-failover';
import { normalizeProtonCertificateFailure } from './proton-certificate';
import type { ProtonHumanVerificationMethod } from './proton-captcha';
import {
  ensureProtonConfgen as ensureProtonConfgenRuntime,
  findProtonConfgenPath,
  protonConfgenCandidates,
  type ProtonRuntimeContext,
} from './proton-runtime';

const moduleDir = path.dirname(fileURLToPath(import.meta.url));

export type ProtonLoginErrorCode =
  | 'INVALID_CREDENTIALS'
  | 'TWO_FACTOR_REQUIRED'
  | 'TWO_FACTOR_INVALID'
  | 'CAPTCHA_REQUIRED'
  | 'CAPTCHA_INVALID'
  | 'CAPTCHA_CANCELLED'
  | 'NETWORK_ERROR'
  | 'TIMEOUT'
  | 'MISSING_EXECUTABLE'
  | 'SESSION_PERSISTENCE'
  | 'CONFIGURATION_ERROR'
  | 'UNKNOWN';

export interface ProtonLoginResult {
  success: boolean;
  username?: string;
  code?: ProtonLoginErrorCode;
  message?: string;
  error?: string;
  retryable?: boolean;
  captchaUrl?: string;
}

export interface ProtonLoginOperation {
  signal: AbortSignal;
  isCurrent: () => boolean;
  cancel: () => void;
  finish: () => void;
}

export interface ProtonLoginCoordinator {
  begin: () => ProtonLoginOperation;
  cancelCurrent: () => void;
}

export type ProtonPlanStatus = 'free' | 'premium' | 'unknown';

export interface ProtonPlanResult {
  success: boolean;
  status: ProtonPlanStatus;
  maxTier?: number;
  planName?: string;
  planTitle?: string;
  checkedAt?: string;
  error?: string;
}

const GENERIC_PLAN_ERROR = 'Não foi possível confirmar o plano Proton.';

export interface ProtonSettings {
  vpnMode: 'proton' | 'custom';
  username: string;
  country: string; // "" for AUTO, or "US", "NL", "JP", etc.
  freeOnly: boolean;
  autoPing: boolean;
  lastServer?: {
    name: string;
    country: string;
    city: string;
    tier: string;
    load: number;
    score: number;
    pingMs: number;
    endpoint: string;
    updatedAt: string;
  };
}

export function findProtonConfgenExe(): string {
  const context = protonRuntimeContext();
  const found = findProtonConfgenPath(context);
  if (found) return found;
  const exeName = process.platform === 'win32' ? 'proton-confgen.exe' : 'proton-confgen';
  throw new Error(`Executável ${exeName} não foi encontrado. Caminhos verificados: ${protonConfgenCandidates(context).slice(0, 4).join(', ')}`);
}

function protonRuntimeContext(): ProtonRuntimeContext {
  let appPath = '';
  try {
    appPath = app.getAppPath();
  } catch {}
  return {
    resourcesPath: process.resourcesPath,
    appPath,
    execPath: process.execPath,
    cwd: process.cwd(),
    moduleDir,
    platform: process.platform,
    arch: process.arch,
  };
}

export function ensureProtonConfgen(installDir: string): Promise<string> {
  let version = process.env.npm_package_version || '0.0.0-dev';
  try {
    if (app && typeof app.getVersion === 'function') version = app.getVersion();
  } catch {}
  return ensureProtonConfgenRuntime({ context: protonRuntimeContext(), installDir, version });
}

export interface RunConfgenOptions {
  args: string[];
  timeoutMs?: number;
  exePath?: string;
  signal?: AbortSignal;
  onProgress?: (progress: ProtonOptimizationProgress) => void;
}

export interface ProtonOptimizationProgress {
  phase: 'ping' | 'catalog' | 'preparing' | 'testing' | 'finalizing' | 'completed' | 'failed' | 'cancelled';
  total: number;
  tested: number;
  succeeded: number;
  server?: string;
  country?: string;
  city?: string;
  tier?: string;
  load?: number;
  score?: number;
  downloadMbps?: number;
  uploadMbps?: number;
  pingMs?: number;
  status?: 'testing' | 'success' | 'failed';
}

function abortError(): Error {
  const error = new Error('Operação Proton cancelada.');
  error.name = 'AbortError';
  return error;
}

import { confgenSecretTransport } from './proton-secrets';

function safeConfgenArgs(args: string[]): string {
  return args.map((arg, index) => {
    const previous = args[index - 1] || '';
    if (/^-?(?:password|2fa|hv-token|session-file)$/i.test(previous)) return '[redacted]';
    if (/^-?username$/i.test(previous)) return '[account]';
    return logger.clipLogText(arg, 160);
  }).join(' ');
}

function validProgress(value: any): ProtonOptimizationProgress | undefined {
  if (!value || typeof value !== 'object') return undefined;
  const phases = ['ping', 'catalog', 'preparing', 'testing', 'finalizing', 'completed', 'failed', 'cancelled'];
  if (!phases.includes(value.phase)) return undefined;
  const total = Number.isFinite(value.total) ? Math.max(0, Math.floor(value.total)) : 0;
  const tested = Number.isFinite(value.tested) ? Math.max(0, Math.min(total, Math.floor(value.tested))) : 0;
  const succeeded = Number.isFinite(value.succeeded) ? Math.max(0, Math.min(tested, Math.floor(value.succeeded))) : 0;
  const result: ProtonOptimizationProgress = { phase: value.phase, total, tested, succeeded };
  for (const key of ['server', 'country', 'city', 'tier'] as const) {
    if (typeof value[key] === 'string' && value[key].length <= 200) result[key] = value[key];
  }
  for (const key of ['downloadMbps', 'uploadMbps', 'pingMs', 'load', 'score'] as const) {
    if (Number.isFinite(value[key]) && value[key] >= 0) result[key] = value[key];
  }
  if (value.status === 'testing' || value.status === 'success' || value.status === 'failed') result.status = value.status;
  return result;
}

export function parseConfgenJson(stdout: string): any | undefined {
  for (const line of stdout.trim().split(/\r?\n/).reverse()) {
    const candidate = line.trim();
    if (!candidate.startsWith('{') && !candidate.startsWith('[')) continue;
    try {
      return JSON.parse(candidate);
    } catch {
      // Linhas de progresso podem se parecer com JSON incompleto; tente a anterior.
    }
  }
  return undefined;
}

export function runConfgen(options: RunConfgenOptions): Promise<{ code: number | null; stdout: string; stderr: string; json?: any }> {
  return new Promise((resolve, reject) => {
    if (options.signal?.aborted) { reject(abortError()); return; }
    let exe: string;
    try {
      exe = options.exePath ? path.resolve(options.exePath) : findProtonConfgenExe();
    } catch (err) {
      reject(err);
      return;
    }

    const timeout = options.timeoutMs ?? 25000;
    const operationId = logger.createOperationId('proton-confgen');
    const startedAt = Date.now();
    const logContext = {
      operation_id: operationId,
      phase: 'confgen',
      executable: path.basename(exe),
      timeout_ms: timeout,
    };
    logger.logEvent('info', 'proton', 'confgen.start', logContext, {
      args: safeConfgenArgs(options.args),
    });
    const transport = confgenSecretTransport(options.args);
    const child = spawn(exe, transport.args, {
      windowsHide: true,
      env: { ...process.env },
    });

    let stdout = '';
    let stderr = '';
    let settled = false;
    let aborted = Boolean(options.signal?.aborted);
    let terminationError: Error | undefined;
    let stderrBuffer = '';
    const stderrDecoder = new StringDecoder('utf8');
    let killTimer: ReturnType<typeof setTimeout> | undefined;
    const emitProgress = (chunk: string) => {
      stderrBuffer += chunk;
      if (stderrBuffer.length > 128 * 1024) stderrBuffer = stderrBuffer.slice(-128 * 1024);
      const lines = stderrBuffer.split(/\r?\n/);
      stderrBuffer = lines.pop() || '';
      for (const line of lines) {
        const match = line.match(/^\s*GOLIVE_PROGRESS\s+(\{.*\})\s*$/);
        if (!match) continue;
        try { const progress = validProgress(JSON.parse(match[1])); if (progress && !terminationError) options.onProgress?.(progress); } catch {}
      }
    };
    const finishReject = (error: Error) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      logger.logEvent('error', 'proton', 'confgen.failed', logContext, {
        durationMs: Date.now() - startedAt,
        error: error.message,
        aborted,
      });
      reject(error);
    };
    const killAndWait = (error: Error) => {
      if (settled || terminationError) return;
      terminationError = error;
      try { child.kill(); } catch { /* Close remains the cleanup boundary. */ }
      killTimer = setTimeout(() => {
        if (!settled) { try { child.kill('SIGKILL'); } catch {} }
      }, 1000);
      killTimer.unref?.();
    };

    const timer = setTimeout(() => {
      killAndWait(new Error(`Tempo limite excedido (${timeout / 1000}s) ao executar proton-confgen.`));
    }, timeout);

    const abort = () => { aborted = true; killAndWait(abortError()); };
    options.signal?.addEventListener('abort', abort, { once: true });

    child.stdout.on('data', (d: Buffer) => {
      stdout += d.toString();
    });

    child.stderr.on('data', (d: Buffer) => {
      const text = stderrDecoder.write(d); stderr += text; emitProgress(text);
    });

    child.on('error', (err) => {
      // Node emits close after spawn errors too. Never remove an executable
      // or staged profile while its process may still be using it.
      terminationError ??= err;
    });

    child.on('close', (code) => {
      if (settled) return;
      const tail = stderrDecoder.end();
      stderr += tail;
      if (stderrBuffer || tail) emitProgress(tail + '\n');
      clearTimeout(timer);
      clearTimeout(killTimer);
      options.signal?.removeEventListener('abort', abort);
      if (terminationError || aborted) { finishReject(terminationError || abortError()); return; }
      const parsedJson = parseConfgenJson(stdout);
      settled = true;
      logger.logEvent(code === 0 ? 'info' : 'warn', 'proton', 'confgen.complete', logContext, {
        durationMs: Date.now() - startedAt,
        exitCode: code,
        stdoutBytes: Buffer.byteLength(stdout, 'utf8'),
        stderrBytes: Buffer.byteLength(stderr, 'utf8'),
        json: Boolean(parsedJson),
        stderrTail: logger.clipLogText(stderr, 1200),
      });
      resolve({ code, stdout, stderr, json: parsedJson });
    });
    child.stdin?.on('error', (error: Error) => {
      if (!settled) killAndWait(new Error('Falha ao enviar credenciais ao helper Proton.'));
    });
    child.stdin?.end(transport.stdin);
    if (aborted) abort();
  });
}

export function classifyProtonError(error: unknown, stderr = '', stdout = ''): { code: ProtonLoginErrorCode; message: string; retryable: boolean } {
  const raw = `${error instanceof Error ? error.message : String(error)} ${stderr} ${stdout}`.toLowerCase();
  if (/captcha_invalid|captcha.*expired|human verification.*(invalid|expired)/.test(raw)) return { code: 'CAPTCHA_INVALID', message: 'A verificação de segurança expirou ou foi recusada. Abra um novo CAPTCHA e tente novamente.', retryable: true };
  if (/captcha_required|captcha verification required|human verification required|code 9001/.test(raw)) return { code: 'CAPTCHA_REQUIRED', message: 'O Proton solicitou uma verificação de segurança. Abra o CAPTCHA e tente novamente.', retryable: true };
  if (/2fa_required|two.?factor|required.*2fa/.test(raw)) return { code: 'TWO_FACTOR_REQUIRED', message: 'Esta conta exige autenticação em duas etapas.', retryable: false };
  if (/2fa|two.?factor|totp|verification code/.test(raw)) return { code: 'TWO_FACTOR_INVALID', message: 'O código 2FA está incorreto ou expirou.', retryable: false };
  // "authentication failed" não é sinal de credencial: o helper Go embrulha
  // qualquer falha de autenticação (transporte, protocolo, captcha) com esse
  // prefixo. Só texto explícito de credencial pode acusar senha errada.
  if (/invalid credential|invalid password|wrong password|incorrect/.test(raw)) return { code: 'INVALID_CREDENTIALS', message: 'Usuário ou senha incorretos.', retryable: false };
  if (/session persistence|session storage could not be updated|failed to (?:migrate|commit|write) session file/.test(raw)) {
    return {
      code: 'SESSION_PERSISTENCE',
      message: 'A sessão Proton já salva não pôde ser atualizada neste computador. Sua senha não foi verificada e a sessão anterior foi preservada. Feche outras versões do aplicativo e tente novamente.',
      retryable: true,
    };
  }
  if (/timeout|tempo limite|timed out/.test(raw)) return { code: 'TIMEOUT', message: 'O ProtonVPN demorou demais para responder. Tente novamente em alguns instantes.', retryable: true };
  if (/encontrado|not found|enoent|spawn/.test(raw)) return { code: 'MISSING_EXECUTABLE', message: 'O componente Proton não pôde ser preparado automaticamente. Verifique sua conexão e tente novamente; se persistir, envie um relatório de diagnóstico.', retryable: true };
  if (/network|connection|dns|tls|temporary|unreachable|reset/.test(raw)) return { code: 'NETWORK_ERROR', message: 'Não foi possível conectar aos servidores ProtonVPN. Verifique sua internet e tente novamente.', retryable: true };
  return { code: 'UNKNOWN', message: 'Não foi possível concluir o login ProtonVPN. Tente novamente ou envie um relatório de diagnóstico.', retryable: true };
}

export function getProtonSessionFile(installDir: string): string {
  return path.join(installDir, 'proton-session.json');
}

function stagedProtonLoginSessionFile(installDir: string): string {
  return path.join(installDir, `.proton-login-${randomUUID().replaceAll('-', '')}.tmp`);
}

function usableStagedSession(file: string): boolean {
  try {
    const stat = fs.lstatSync(file);
    return stat.isFile() && !stat.isSymbolicLink() && stat.size > 0;
  } catch {
    return false;
  }
}

function cancelledLoginResult(): ProtonLoginResult {
  const message = 'Esta tentativa de login foi cancelada ou substituída por outra.';
  return { success: false, code: 'CAPTCHA_CANCELLED', message, error: message, retryable: true };
}

/** Read only the non-secret identity through the sidecar's locked session contract. */
export async function getSavedSessionUsername(installDir: string): Promise<string> {
  try {
    ensureInstallDir(installDir);
    const exePath = await ensureProtonConfgen(installDir);
    const res = await runConfgen({
      args: [
        '-session-file',
        getProtonSessionFile(installDir),
        '-session-username',
        '-json',
      ],
      timeoutMs: 10_000,
      exePath,
    });
    if (res.code !== 0 || res.json?.success !== true || typeof res.json.username !== 'string') return '';
    return res.json.username.trim();
  } catch {
    return '';
  }
}

export type ProtonSessionConfirmation = {
  confirmed: boolean;
  savedUsername: string;
  attempts: number;
};

function ensureInstallDir(installDir: string) {
  fs.mkdirSync(installDir, { recursive: true });
}

/**
 * Contrato de `-check-session`. `retryable` marca falha de verificacao (rede,
 * timeout, helper ausente) que NAO invalida a sessao: a GUI mantem o usuario
 * autenticado e a ativacao continua disponivel com o perfil ja gerado.
 */
export type ProtonSessionCheckCode = 'INVALID_SESSION' | 'NETWORK_ERROR' | 'TIMEOUT' | 'MISSING_EXECUTABLE' | 'SESSION_PERSISTENCE' | 'UNKNOWN';

export interface ProtonSessionCheckResult {
  valid: boolean;
  username?: string;
  expiresIn?: string;
  code?: ProtonSessionCheckCode;
  retryable?: boolean;
  error?: string;
}

/**
 * Accept only the small, non-secret contract emitted by -check-plan. In
 * particular, a missing/invalid MaxTier is never interpreted as Free.
 */
export function normalizeProtonPlanResult(value: any): ProtonPlanResult {
  if (!value || typeof value !== 'object' || value.success !== true) {
    return { success: false, status: 'unknown', error: GENERIC_PLAN_ERROR };
  }

  const maxTier = value.maxTier;
  if (!Number.isInteger(maxTier) || maxTier < 0) {
    return { success: false, status: 'unknown', error: GENERIC_PLAN_ERROR };
  }

  const result: ProtonPlanResult = {
    success: true,
    status: maxTier === 0 ? 'free' : 'premium',
    maxTier,
  };
  for (const key of ['planName', 'planTitle'] as const) {
    if (typeof value[key] === 'string' && value[key].trim() && value[key].length <= 120) {
      result[key] = value[key].trim();
    }
  }
  if (typeof value.checkedAt === 'string' && value.checkedAt.length <= 40) {
    result.checkedAt = value.checkedAt;
  }
  return result;
}

/**
 * Queries the account plan through the saved Proton session only. This does
 * not request a certificate, select a server, or create a WireGuard tunnel.
 */
export async function getProtonPlan(installDir: string, username: string): Promise<ProtonPlanResult> {
  if (!username || !username.trim()) {
    return { success: false, status: 'unknown', error: 'Sessão Proton não encontrada.' };
  }

  ensureInstallDir(installDir);
  const sessionFile = getProtonSessionFile(installDir);
  let exePath: string;
  try {
    exePath = await ensureProtonConfgen(installDir);
  } catch {
    return { success: false, status: 'unknown', error: GENERIC_PLAN_ERROR };
  }
  let res;
  try {
    res = await runConfgen({
      args: [
        '-username',
        username.trim(),
        '-session-file',
        sessionFile,
        '-check-plan',
        '-json',
      ],
      timeoutMs: 10000,
      exePath,
    });
  } catch {
    return { success: false, status: 'unknown', error: GENERIC_PLAN_ERROR };
  }

  if (res.code !== 0 || !res.json) {
    logger.warn('proton', 'falha ao consultar plano ProtonVPN', {
      codigo_saida: res.code,
      resposta_json: Boolean(res.json),
    });
    return { success: false, status: 'unknown', error: GENERIC_PLAN_ERROR };
  }

  const normalized = normalizeProtonPlanResult(res.json);
  return { ...normalized, checkedAt: new Date().toISOString() };
}

export async function loginProton(
  installDir: string,
  username: string,
  password?: string,
  twoFactorCode?: string,
  humanVerificationToken?: string,
  operation?: Pick<ProtonLoginOperation, 'signal' | 'isCurrent'>,
  humanVerificationMethod?: ProtonHumanVerificationMethod,
): Promise<ProtonLoginResult> {
  const operationIsCurrent = () => !operation?.signal.aborted && (operation?.isCurrent?.() ?? true);
  if (!operationIsCurrent()) return cancelledLoginResult();
  try {
    ensureInstallDir(installDir);
  } catch (error) {
    const classified = classifyProtonError(error);
    return { success: false, ...classified, code: 'SESSION_PERSISTENCE', message: 'Não foi possível preparar a pasta de dados para salvar a sessão ProtonVPN.', retryable: false, error: error instanceof Error ? error.message : String(error) };
  }
  let exePath: string;
  try {
    exePath = await ensureProtonConfgen(installDir);
  } catch (error) {
    const classified = classifyProtonError(error);
    return { success: false, ...classified, error: error instanceof Error ? error.message : String(error) };
  }
  if (!operationIsCurrent()) return cancelledLoginResult();
  const sessionFile = getProtonSessionFile(installDir);
  const stagedSessionFile = stagedProtonLoginSessionFile(installDir);
  try {
  const args = [
    '-username',
    username,
    '-session-file',
    stagedSessionFile,
    '-login-only',
    '-json',
  ];

  if (password) {
    args.push('-password', password);
  }
  if (twoFactorCode) {
    args.push('-2fa', twoFactorCode);
  }
  if (humanVerificationToken) {
    args.push('-hv-token', humanVerificationToken);
    args.push('-hv-method', humanVerificationMethod ?? 'captcha');
  }

  logger.info('proton', 'iniciando autenticação ProtonVPN');
  let res;
  try {
    res = await runConfgen({ args, timeoutMs: 25000, signal: operation?.signal, exePath });
  } catch (error) {
    if (!operationIsCurrent() || (error instanceof Error && error.name === 'AbortError')) return cancelledLoginResult();
    const classified = classifyProtonError(error);
    logger.error('proton', 'falha ao iniciar proton-confgen', { codigo: classified.code, erro: error instanceof Error ? error.message : String(error) });
    return { success: false, ...classified, error: error instanceof Error ? error.message : String(error) };
  }

  if (!operationIsCurrent()) return cancelledLoginResult();

  if (res.json && res.json.success) {
    if (!usableStagedSession(stagedSessionFile)) {
      return {
        success: false,
        code: 'SESSION_PERSISTENCE',
        message: 'A autenticação terminou, mas a sessão Proton não pôde ser preparada.',
        error: 'O helper Proton não produziu uma sessão válida.',
        retryable: true,
      };
    }
    // Keep the current-operation check adjacent to the synchronous rename: no
    // cancelled generation can interleave and promote its staged session.
    if (!operationIsCurrent()) return cancelledLoginResult();
    try {
      fs.renameSync(stagedSessionFile, sessionFile);
    } catch (error) {
      return {
        success: false,
        code: 'SESSION_PERSISTENCE',
        message: 'A sessão Proton foi criada, mas não pôde ser salva neste computador.',
        error: error instanceof Error ? error.message : String(error),
        retryable: true,
      };
    }
    logger.info('proton', 'autenticação bem-sucedida');
    return {
      success: true,
      username: typeof res.json.username === 'string' && res.json.username.trim()
        ? res.json.username.trim()
        : username.trim(),
      message: 'Autenticação concluída.',
    };
  }

  if (res.json?.code === 'CAPTCHA_REQUIRED' || res.json?.code === 'CAPTCHA_INVALID') {
    return {
      success: false,
      code: res.json.code,
      message: res.json.error || (res.json.code === 'CAPTCHA_INVALID'
        ? 'A verificação de segurança expirou ou foi recusada.'
        : 'O Proton solicitou uma verificação de segurança.'),
      retryable: res.json.retryable !== false,
      captchaUrl: typeof res.json.captchaUrl === 'string' ? res.json.captchaUrl : undefined,
      error: res.json.error || (res.json.code === 'CAPTCHA_INVALID'
        ? 'A verificação de segurança expirou ou foi recusada.'
        : 'O Proton solicitou uma verificação de segurança.'),
    };
  }

  // O helper Go emite códigos estruturados no JSON (ver jsonErrorResponse).
  // Eles são a fonte da verdade; o regex de texto é só fallback para saída
  // sem JSON (binários antigos) e nunca pode mascarar o código estruturado.
  const structuredCode = typeof res.json?.code === 'string' ? res.json.code : '';
  const structured = structuredProtonError(structuredCode);
  if (structured) {
    logger.warn('proton', 'falha na autenticação ProtonVPN', { codigo_saida: res.code, codigo_estruturado: structuredCode, erro_json: String(res.json?.error || '').slice(0, 300) });
    return { success: false, code: structuredCode as ProtonLoginErrorCode, ...structured, error: structured.message };
  }

  const errorMsg = res.json?.error || res.stderr || res.stdout || 'Falha na autenticação ProtonVPN.';
  const classified = classifyProtonError(errorMsg, res.stderr, res.stdout);
  logger.warn('proton', 'falha na autenticação ProtonVPN', { codigo_saida: res.code, resposta_json: Boolean(res.json), erro_json: String(res.json?.error || '').slice(0, 300) });
  return { success: false, ...classified, error: classified.message };
  } finally {
    // runConfgen settles only after the child closes, so cleanup cannot race a
    // cancelled helper that is still writing its private staged file.
    try { fs.rmSync(stagedSessionFile, { force: true }); } catch {}
    try { fs.rmSync(`${stagedSessionFile}.lock`, { force: true }); } catch {}
  }
}

function structuredProtonError(code: string): { message: string; retryable: boolean } | undefined {
  switch (code) {
    case 'INVALID_CREDENTIALS':
      return { message: 'Usuário ou senha incorretos.', retryable: false };
    case 'TWO_FACTOR_REQUIRED':
      return { message: 'Esta conta exige autenticação em duas etapas.', retryable: false };
    case 'TWO_FACTOR_INVALID':
      return { message: 'O código 2FA está incorreto ou expirou.', retryable: false };
    case 'NETWORK_ERROR':
      return { message: 'Não foi possível conectar aos servidores ProtonVPN. Verifique sua internet e tente novamente.', retryable: true };
    case 'SESSION_PERSISTENCE':
      return {
        message: 'A sessão Proton já salva não pôde ser atualizada neste computador. Sua senha não foi verificada e a sessão anterior foi preservada. Feche outras versões do aplicativo e tente novamente.',
        retryable: true,
      };
    default:
      return undefined;
  }
}

export interface ProtonConfigResult {
  success: boolean;
  server?: string;
  country?: string;
  city?: string;
  tier?: string;
  load?: number;
  score?: number;
  pingMs?: number;
  downloadMbps?: number;
  uploadMbps?: number;
  speedTested?: number;
  speedSucceeded?: number;
  endpoint?: string;
  confFile?: string;
  code?: ProtonLoginErrorCode;
  retryable?: boolean;
  captchaUrl?: string;
  error?: string;
}

export async function generateOptimalProtonConfig(
  installDir: string,
  options: {
    username: string;
    countries?: string;
    freeOnly?: boolean;
    autoPing?: boolean;
    speedTest?: boolean;
    signal?: AbortSignal;
    onProgress?: (progress: ProtonOptimizationProgress) => void;
    humanVerificationToken?: string;
    humanVerificationMethod?: ProtonHumanVerificationMethod;
  }
): Promise<ProtonConfigResult> {
  const sessionFile = getProtonSessionFile(installDir);
  const outputFile = path.join(installDir, 'wireguard.conf');
  ensureInstallDir(installDir);
  const stagingFile = path.join(installDir, `.wireguard.conf.${randomUUID()}.tmp`);
  let exePath: string;
  try {
    exePath = await ensureProtonConfgen(installDir);
  } catch (error) {
    return { success: false, error: error instanceof Error ? error.message : String(error) };
  }

  const args = [
    '-username',
    options.username,
    '-session-file',
    sessionFile,
    '-output',
    stagingFile,
    '-json',
    '-ipv6',
    '-exclude-countries',
    'BR',
  ];

  if (options.autoPing !== false) {
    args.push('-auto-ping');
  }
  if (options.speedTest) args.push('-speed-test');

  if (options.freeOnly !== false) {
    args.push('-free-only');
  }

  if (options.countries && options.countries.trim()) {
    args.push('-countries', options.countries.trim());
  }
  if (options.humanVerificationToken) {
    args.push('-hv-token', options.humanVerificationToken);
    args.push('-hv-method', options.humanVerificationMethod ?? 'captcha');
  }

  logger.info('proton', 'gerando configuração ótima WireGuard ProtonVPN', {
    country: options.countries || 'AUTO',
    autoPing: options.autoPing !== false,
  });

  // Older WireSock filters may include the normal helper. A uniquely named
  // copy outside Discord directories also avoids nesting with those profiles.
  let res;
  try {
    res = options.speedTest
      ? await runIsolatedSpeedSelection(args, options.signal, options.onProgress, exePath)
      : await runConfgen({ args, timeoutMs: 60000, signal: options.signal, onProgress: options.onProgress, exePath });
  } catch (error) {
    try { fs.rmSync(stagingFile, { force: true }); } catch {}
    throw error;
  }

  const measuredResultValid = !options.speedTest ||
    (finitePositive(res.json?.downloadMbps) && finitePositive(res.json?.uploadMbps));
  if (res.code === 0 && res.json && res.json.success && measuredResultValid && fs.existsSync(stagingFile)) {
    if (options.signal?.aborted) {
      try { fs.rmSync(stagingFile, { force: true }); } catch {}
      throw abortError();
    }
    try { fs.renameSync(stagingFile, outputFile); }
    catch (error) {
      try { fs.rmSync(stagingFile, { force: true }); } catch {}
      throw error;
    }
    logger.info('proton', 'servidor ótimo selecionado com sucesso', {
      server: res.json.server,
      ping: res.json.pingMs,
      load: res.json.load,
      downloadMbps: res.json.downloadMbps,
      uploadMbps: res.json.uploadMbps,
      speedTested: res.json.speedTested,
      speedSucceeded: res.json.speedSucceeded,
    });
    return {
      success: true,
      server: res.json.server,
      country: res.json.country,
      city: res.json.city,
      tier: res.json.tier,
      load: res.json.load,
      score: res.json.score,
      pingMs: res.json.pingMs,
      downloadMbps: res.json.downloadMbps,
      uploadMbps: res.json.uploadMbps,
      speedTested: res.json.speedTested,
      speedSucceeded: res.json.speedSucceeded,
      endpoint: res.json.endpoint,
      confFile: outputFile,
    };
  }

  try { fs.rmSync(stagingFile, { force: true }); } catch {}
  const errMsg = res.json?.error || (options.speedTest && res.json?.success
    ? 'A medição não retornou velocidades válidas de download e upload.'
    : undefined) || res.stderr || res.stdout || 'Falha ao selecionar e gerar configuração ProtonVPN.';
  // Sem a mensagem do helper o relato de bug chega sem a causa: o motivo já
  // existe em errMsg e era descartado, deixando só "codigo_saida=1". A linha é
  // normalizada e limitada porque é copiada para o ring buffer e para a issue.
  logger.error('proton', 'erro ao gerar configuração ótima', {
    codigo_saida: res.code,
    resposta_json: Boolean(res.json),
    codigo: typeof res.json?.code === 'string' ? res.json.code : undefined,
    medicao_valida: options.speedTest ? measuredResultValid : undefined,
    erro: errMsg.replace(/\s+/g, ' ').trim().slice(0, 300),
  });
  return normalizeProtonCertificateFailure(res.json, errMsg) as ProtonConfigResult;
}

export interface ProtonManualRouteOptions {
  username: string;
  server: string;
  countries?: string;
  freeOnly?: boolean;
  autoPing?: boolean;
  signal?: AbortSignal;
  humanVerificationToken?: string;
  humanVerificationMethod?: ProtonHumanVerificationMethod;
}

export interface ProtonManualRouteResult {
  success: boolean;
  manual?: boolean;
  server?: string;
  country?: string;
  city?: string;
  tier?: string;
  load?: number;
  score?: number;
  pingMs?: number;
  endpoint?: string;
  preflight?: string;
  confFile?: string;
  staged?: boolean;
  error?: string;
  code?: ProtonLoginErrorCode;
  retryable?: boolean;
  captchaUrl?: string;
}

export interface ProtonRouteCatalogEntry {
  server: string;
  country: string;
  city: string;
  tier: string;
  load: number;
  score: number;
  pingMs?: number;
}

export interface ProtonRouteCatalogResult {
  success: boolean;
  routes?: ProtonRouteCatalogEntry[];
  error?: string;
}

export interface ProtonRoutePoolResult {
  success: boolean;
  stagingDir?: string;
  routes?: ProtonRouteMetadata[];
  expiresAt?: number;
  error?: string;
}

export async function runIsolatedSpeedSelection(args: string[], signal?: AbortSignal, onProgress?: (progress: ProtonOptimizationProgress) => void, sourceExePath?: string) {
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), 'golive-speed-'));
  const exePath = path.join(tempDir, process.platform === 'win32' ? 'golive-speed-probe.exe' : 'golive-speed-probe');
  try {
    fs.copyFileSync(sourceExePath || findProtonConfgenExe(), exePath);
    if (process.platform !== 'win32') fs.chmodSync(exePath, 0o700);
    return await runConfgen({ args: [...args, '-progress-json'], exePath, timeoutMs: 210000, signal, onProgress });
  } finally {
    // Windows may need a moment to release the executable after timeout/exit.
    await fs.promises.rm(tempDir, { recursive: true, force: true, maxRetries: 12, retryDelay: 200 });
  }
}

function finitePositive(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0;
}


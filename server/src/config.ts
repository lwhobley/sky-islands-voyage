import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

export const APP_CONFIG = Symbol('APP_CONFIG');

export interface AppleConfig {
  bundleId: string;
  issuerId: string;
  keyId: string;
  privateKey: string; // contents of the App Store Connect .p8 key
  environment: 'Sandbox' | 'Production';
  appAppleId?: number; // required for Production
  rootCertificates: Buffer[]; // Apple root CAs (.cer, DER) used to verify signed transactions
}

export interface GoogleConfig {
  packageName: string;
  serviceAccountKeyFile: string;
}

export interface FirebaseConfig {
  serviceAccountKeyFile: string;
}

export interface AppConfig {
  port: number;
  supabaseUrl: string;
  supabaseServiceKey: string;
  apple?: AppleConfig;
  google?: GoogleConfig;
  firebase?: FirebaseConfig;
  pushPollMs: number;
  /** Shared secret for the /admin API; the admin API is disabled when unset. */
  adminApiKey?: string;
}

/**
 * Reads configuration from the environment. Supabase is required; each store and push
 * integration is enabled only when all of its variables are set (partial config is an error).
 */
export function loadConfig(env: NodeJS.ProcessEnv = process.env): AppConfig {
  return {
    port: Number(env.PORT ?? 3000),
    supabaseUrl: required(env, 'SUPABASE_URL'),
    supabaseServiceKey: required(env, 'SUPABASE_SERVICE_ROLE_KEY'),
    apple: group(env, ['APPLE_BUNDLE_ID', 'APPLE_ISSUER_ID', 'APPLE_KEY_ID', 'APPLE_PRIVATE_KEY_PATH', 'APPLE_ROOT_CERTS_DIR'], (e) => {
      const environment = e.APPLE_ENVIRONMENT ?? 'Sandbox';
      if (environment !== 'Sandbox' && environment !== 'Production') {
        throw new Error('APPLE_ENVIRONMENT must be Sandbox or Production');
      }
      if (environment === 'Production' && !e.APPLE_APP_ID) {
        throw new Error('APPLE_APP_ID is required when APPLE_ENVIRONMENT=Production');
      }
      const certDir = e.APPLE_ROOT_CERTS_DIR!;
      const rootCertificates = readdirSync(certDir)
        .filter((f) => f.toLowerCase().endsWith('.cer'))
        .map((f) => readFileSync(join(certDir, f)));
      if (rootCertificates.length === 0) throw new Error(`No .cer files in APPLE_ROOT_CERTS_DIR (${certDir})`);
      return {
        bundleId: e.APPLE_BUNDLE_ID!,
        issuerId: e.APPLE_ISSUER_ID!,
        keyId: e.APPLE_KEY_ID!,
        privateKey: readFileSync(e.APPLE_PRIVATE_KEY_PATH!, 'utf8'),
        environment,
        appAppleId: e.APPLE_APP_ID ? Number(e.APPLE_APP_ID) : undefined,
        rootCertificates,
      };
    }),
    google: group(env, ['GOOGLE_PACKAGE_NAME', 'GOOGLE_SERVICE_ACCOUNT_KEY_FILE'], (e) => ({
      packageName: e.GOOGLE_PACKAGE_NAME!,
      serviceAccountKeyFile: e.GOOGLE_SERVICE_ACCOUNT_KEY_FILE!,
    })),
    firebase: group(env, ['FIREBASE_SERVICE_ACCOUNT_KEY_FILE'], (e) => ({
      serviceAccountKeyFile: e.FIREBASE_SERVICE_ACCOUNT_KEY_FILE!,
    })),
    pushPollMs: Number(env.PUSH_POLL_MS ?? 3000),
    adminApiKey: adminKey(env.ADMIN_API_KEY),
  };
}

function adminKey(value: string | undefined): string | undefined {
  if (!value) return undefined;
  if (value.length < 32) throw new Error('ADMIN_API_KEY must be at least 32 characters');
  return value;
}

function required(env: NodeJS.ProcessEnv, name: string): string {
  const value = env[name];
  if (!value) throw new Error(`Missing required environment variable ${name}`);
  return value;
}

function group<T>(env: NodeJS.ProcessEnv, names: string[], build: (env: NodeJS.ProcessEnv) => T): T | undefined {
  const missing = names.filter((n) => !env[n]);
  if (missing.length === names.length) return undefined;
  if (missing.length > 0) throw new Error(`Incomplete configuration, missing: ${missing.join(', ')}`);
  return build(env);
}

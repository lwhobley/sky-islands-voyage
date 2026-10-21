import { cert, initializeApp } from 'firebase-admin/app';
import { getMessaging, Messaging } from 'firebase-admin/messaging';
import { FirebaseConfig } from '../config';

export const PUSH_SENDER = Symbol('PUSH_SENDER');

export interface PushMessage {
  title: string;
  body: string;
  priority: 'high' | 'normal';
  data: Record<string, string>;
}

export interface PushResult {
  successCount: number;
  /** Tokens FCM says will never work again; they are deleted from push_tokens. */
  invalidTokens: string[];
  /** Some tokens failed for a retryable reason (quota, unavailable, ...). */
  transientFailure: boolean;
}

export interface PushSender {
  send(tokens: string[], message: PushMessage): Promise<PushResult>;
}

const DEAD_TOKEN_CODES = new Set([
  'messaging/registration-token-not-registered',
  'messaging/invalid-registration-token',
]);

export class FcmPushSender implements PushSender {
  private readonly messaging: Messaging;

  constructor(config: FirebaseConfig) {
    const app = initializeApp({ credential: cert(config.serviceAccountKeyFile) }, 'sky-islands-push');
    this.messaging = getMessaging(app);
  }

  async send(tokens: string[], message: PushMessage): Promise<PushResult> {
    const high = message.priority === 'high';
    const res = await this.messaging.sendEachForMulticast({
      tokens,
      notification: { title: message.title, body: message.body },
      data: message.data,
      android: { priority: high ? 'high' : 'normal', notification: { channelId: 'raids' } },
      apns: { headers: { 'apns-priority': high ? '10' : '5' }, payload: { aps: { sound: 'default' } } },
    });

    const invalidTokens: string[] = [];
    let transientFailure = false;
    res.responses.forEach((r, i) => {
      if (r.success) return;
      if (r.error && DEAD_TOKEN_CODES.has(r.error.code)) invalidTokens.push(tokens[i]);
      else transientFailure = true;
    });
    return { successCount: res.successCount, invalidTokens, transientFailure };
  }
}

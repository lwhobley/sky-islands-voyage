import { CanActivate, createParamDecorator, ExecutionContext, ForbiddenException, Inject, Injectable, UnauthorizedException } from '@nestjs/common';
import { createHash, timingSafeEqual } from 'node:crypto';
import type { Request } from 'express';
import { APP_CONFIG, AppConfig } from '../config';

type AdminRequest = Request & { adminActor?: string };

/**
 * Live-ops access: `X-Admin-Key: <ADMIN_API_KEY>` plus `X-Admin-Actor: <operator>`, which is
 * written to public.admin_actions with every change. Put this behind your VPN / SSO proxy too.
 */
@Injectable()
export class AdminGuard implements CanActivate {
  private readonly keyDigest?: Buffer;

  constructor(@Inject(APP_CONFIG) config: AppConfig) {
    if (config.adminApiKey) this.keyDigest = digest(config.adminApiKey);
  }

  canActivate(context: ExecutionContext): boolean {
    if (!this.keyDigest) throw new ForbiddenException('admin_api_disabled');
    const req = context.switchToHttp().getRequest<AdminRequest>();
    const key = req.header('x-admin-key');
    // Compare fixed-length digests so neither content nor length leaks through timing.
    if (!key || !timingSafeEqual(digest(key), this.keyDigest)) throw new UnauthorizedException('invalid_admin_key');
    const actor = req.header('x-admin-actor')?.trim();
    if (!actor || actor.length > 100) throw new UnauthorizedException('admin_actor_required');
    req.adminActor = actor;
    return true;
  }
}

export const AdminActor = createParamDecorator(
  (_: unknown, context: ExecutionContext): string => context.switchToHttp().getRequest<AdminRequest>().adminActor!,
);

function digest(value: string): Buffer {
  return createHash('sha256').update(value).digest();
}

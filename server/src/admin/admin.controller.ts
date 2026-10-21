import {
  BadRequestException,
  Body,
  ConflictException,
  Controller,
  Get,
  HttpCode,
  HttpStatus,
  NotFoundException,
  Param,
  Post,
  Query,
  UseGuards,
} from '@nestjs/common';
import { RpcError, SupabaseService } from '../supabase.service';
import { AdminActor, AdminGuard } from './admin.guard';

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

@Controller('admin')
@UseGuards(AdminGuard)
export class AdminController {
  constructor(private readonly supabase: SupabaseService) {}

  /**
   * GET /admin/anomalies?minRolls=200&minZ=4
   * Players whose win rate at >=10x multipliers is statistically above their rate at <=2x —
   * the signature of a modified client that peeks at upcoming rolls. Review before acting.
   */
  @Get('anomalies')
  anomalies(@Query('minRolls') minRolls?: string, @Query('minZ') minZ?: string) {
    return this.supabase.rpc('admin_multiplier_anomalies', {
      p_min_rolls: intParam(minRolls, 200, 50, 1_000_000, 'minRolls'),
      p_min_z: numberParam(minZ, 4, 'minZ'),
    });
  }

  /**
   * POST /admin/grants  { userId, rolls?, cardPacks?, crystals?, reason }
   * Compensation / goodwill grant, recorded in public.admin_actions with the operator.
   * Returns the player's new state.
   */
  @Post('grants')
  @HttpCode(HttpStatus.OK)
  async grant(@AdminActor() actor: string, @Body() body: unknown) {
    const b = (body ?? {}) as Record<string, unknown>;
    if (typeof b.userId !== 'string' || !UUID.test(b.userId)) throw new BadRequestException('userId must be a UUID');
    if (typeof b.reason !== 'string' || b.reason.trim().length === 0) throw new BadRequestException('reason is required');
    const crystals = b.crystals ?? '0';
    if (typeof crystals !== 'string' || !/^\d{1,40}$/.test(crystals)) {
      throw new BadRequestException('crystals must be a non-negative integer string');
    }
    try {
      return await this.supabase.rpc('admin_grant', {
        p_actor: actor,
        p_user_id: b.userId,
        p_rolls: wholeNumber(b.rolls, 'rolls'),
        p_card_packs: wholeNumber(b.cardPacks, 'cardPacks'),
        p_crystals: crystals,
        p_reason: b.reason.trim().slice(0, 500),
      });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'player_not_found') throw new NotFoundException(e.message);
      if (e instanceof RpcError && (e.message === 'grant_out_of_bounds' || e.message === 'actor_and_reason_required')) {
        throw new BadRequestException(e.message);
      }
      throw e;
    }
  }
}

@Controller('admin/reports')
@UseGuards(AdminGuard)
export class AdminReportsController {
  constructor(private readonly supabase: SupabaseService) {}

  /** GET /admin/reports?status=open&limit=50 — oldest first, with open-report counts per player. */
  @Get()
  list(@Query('status') status = 'open', @Query('limit') limit?: string) {
    if (!['open', 'actioned', 'dismissed'].includes(status)) {
      throw new BadRequestException('status must be open, actioned or dismissed');
    }
    return this.supabase.rpc('admin_list_reports', { p_status: status, p_limit: intParam(limit, 50, 1, 200, 'limit') });
  }

  /**
   * POST /admin/reports/:id/resolve  { status: "actioned"|"dismissed", note, resetName?, deleteMessage? }
   * resetName renames the reported player to "Voyager NNNN"; deleteMessage removes the pinned chat message.
   */
  @Post(':id/resolve')
  @HttpCode(HttpStatus.OK)
  async resolve(@AdminActor() actor: string, @Param('id') id: string, @Body() body: unknown) {
    const b = (body ?? {}) as Record<string, unknown>;
    if (!/^\d{1,18}$/.test(id)) throw new BadRequestException('id must be a report id');
    if (b.status !== 'actioned' && b.status !== 'dismissed') throw new BadRequestException('status must be actioned or dismissed');
    if (typeof b.note !== 'string' || b.note.trim().length === 0) throw new BadRequestException('note is required');
    for (const flag of ['resetName', 'deleteMessage']) {
      if (b[flag] !== undefined && typeof b[flag] !== 'boolean') throw new BadRequestException(`${flag} must be a boolean`);
    }
    try {
      return await this.supabase.rpc('admin_resolve_report', {
        p_actor: actor,
        p_report_id: Number(id),
        p_status: b.status,
        p_note: b.note.trim().slice(0, 500),
        p_reset_name: b.resetName === true,
        p_delete_message: b.deleteMessage === true,
      });
    } catch (e) {
      if (e instanceof RpcError && e.message === 'report_not_found') throw new NotFoundException(e.message);
      if (e instanceof RpcError && e.message === 'report_already_resolved') throw new ConflictException(e.message);
      throw e;
    }
  }
}

function wholeNumber(value: unknown, name: string): number {
  if (value === undefined) return 0;
  if (typeof value !== 'number' || !Number.isInteger(value) || value < 0) {
    throw new BadRequestException(`${name} must be a non-negative integer`);
  }
  return value;
}

function intParam(value: string | undefined, fallback: number, min: number, max: number, name: string): number {
  if (value === undefined) return fallback;
  const n = Number(value);
  if (!Number.isInteger(n) || n < min || n > max) throw new BadRequestException(`${name} must be an integer in ${min}..${max}`);
  return n;
}

function numberParam(value: string | undefined, fallback: number, name: string): number {
  if (value === undefined) return fallback;
  const n = Number(value);
  if (!Number.isFinite(n)) throw new BadRequestException(`${name} must be a number`);
  return n;
}

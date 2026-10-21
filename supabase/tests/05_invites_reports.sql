\set ON_ERROR_STOP 1
-- Guild invite codes and player reports. Runs after 01-04.
-- State going in: Alpha (1111) leads "Full House" (30 members, open); Bravo (2222) and
-- Charlie (3333) are guildless.

-- ---------- invite codes ----------
set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ declare g jsonb; begin
  g := public.create_guild('Sky Corsairs', 'SKY', 'Invite only');
  assert g->>'invite_code' ~ '^[0-9A-F]{8}$', 'leaders see the code: ' || g::text;
  g := public.update_guild_settings(false, 'Invite only. Ask in Discord.');
  assert not (g->>'is_open')::boolean and g->>'description' = 'Invite only. Ask in Discord.', g::text;
  begin perform public.update_guild_settings(true, 'Official guild'); raise exception 'banned term';
  exception when others then assert sqlerrm = 'name_not_allowed', sqlerrm; end;
  -- the code is not readable through the table itself
  begin perform invite_code from public.guilds; raise exception 'code leaked via REST';
  exception when insufficient_privilege then null; end;
  assert (select count(*) from public.guilds) >= 1, 'other guild columns stay browsable';
end $$;
reset role;
create temp table code as select invite_code from public.guilds where tag = 'SKY';
grant select on code to authenticated;

set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare g jsonb; c text := (select invite_code from code); begin
  assert jsonb_array_length(public.list_guilds('corsair')) = 0, 'closed guilds are not listed';
  begin perform public.join_guild((select id from public.guilds where tag = 'SKY')); raise exception 'closed';
  exception when others then assert sqlerrm = 'guild_closed', sqlerrm; end;
  begin perform public.join_guild_by_code('NOPE1234'); raise exception 'bad code';
  exception when others then assert sqlerrm = 'invalid_invite_code', sqlerrm; end;
  g := public.join_guild_by_code(' ' || lower(c) || ' ');  -- trimmed, case-insensitive
  assert g->>'tag' = 'SKY' and g->>'my_role' = 'member', g::text;
  assert g->>'invite_code' is null, 'plain members do not see the code';
  begin perform public.rotate_guild_invite_code(); raise exception 'member rotate';
  exception when others then assert sqlerrm = 'insufficient_guild_role', sqlerrm; end;
  perform public.send_guild_message('you are all terrible');
end $$;
reset role;

set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ declare g jsonb; old text := (select invite_code from code); begin
  g := public.rotate_guild_invite_code();
  assert g->>'invite_code' <> old, 'code rotated';
end $$;
reset role;
-- the 30-member cap applies to invite joins too ("Full House" from 03 is full)
create temp table full_code as select invite_code from public.guilds where name = 'Full House';
grant select on full_code to authenticated;
set role supabase_auth_admin;
insert into auth.users (id) values ('44444444-4444-4444-4444-444444444444');
reset role;
set request.jwt.claim.sub = '44444444-4444-4444-4444-444444444444';
set role authenticated;
do $$ begin
  begin perform public.join_guild_by_code((select invite_code from full_code)); raise exception 'cap via code';
  exception when others then assert sqlerrm = 'guild_full', sqlerrm; end;
end $$;
reset role;

-- ---------- reports ----------
set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ declare mid bigint; begin
  select id into mid from public.guild_messages where body = 'you are all terrible';
  begin perform public.report_player('33333333-3333-3333-3333-333333333333', 'spam'); raise exception 'self';
  exception when others then assert sqlerrm = 'cannot_report_self', sqlerrm; end;
  begin perform public.report_player('22222222-2222-2222-2222-222222222222', 'rude'); raise exception 'reason';
  exception when others then assert sqlerrm = 'invalid_report', sqlerrm; end;
  begin perform public.report_player('11111111-1111-1111-1111-111111111111', 'harassment', '', mid); raise exception 'wrong author';
  exception when others then assert sqlerrm = 'message_not_found', sqlerrm; end;
  perform public.report_player('22222222-2222-2222-2222-222222222222', 'harassment', 'insulting the guild', mid);
  perform public.report_player('22222222-2222-2222-2222-222222222222', 'harassment', 'again', mid);  -- duplicate: no-op
  begin perform 1 from public.player_reports; raise exception 'reports hidden';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
do $$ begin
  assert (select count(*) from public.player_reports) = 1, 'duplicate open report ignored';
  assert (select message_snapshot from public.player_reports) = 'you are all terrible';
end $$;
-- Alpha can't pin a message from a guild they're not in (Alpha can't even see its id; take it as superuser)
create temp table evidence as select id from public.guild_messages where body = 'you are all terrible';
grant select on evidence to authenticated;
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ begin
  perform public.report_player('22222222-2222-2222-2222-222222222222', 'offensive_name');
  begin
    perform public.report_player('22222222-2222-2222-2222-222222222222', 'harassment', '', (select id from evidence));
    raise exception 'outsider evidence';
  exception when others then assert sqlerrm = 'message_not_found', sqlerrm; end;
  for k in 1..9 loop
    perform public.report_player('33333333-3333-3333-3333-333333333333', 'other', 'filler ' || k);
  end loop;
end $$;
reset role;
-- duplicates are blocked by the unique index, so make 9 distinct-ish rows bypass it for the rate test
update public.player_reports set status = 'dismissed'
 where reporter_id = '11111111-1111-1111-1111-111111111111' and reported_id = '33333333-3333-3333-3333-333333333333';
insert into public.player_reports (reporter_id, reported_id, reason, reported_name, status)
select '11111111-1111-1111-1111-111111111111', '33333333-3333-3333-3333-333333333333', 'other', 'x', 'dismissed'
  from generate_series(1, 9);
set role authenticated;
do $$ begin
  begin perform public.report_player('33333333-3333-3333-3333-333333333333', 'spam'); raise exception 'rate';
  exception when others then assert sqlerrm = 'rate_limited', sqlerrm; end;
end $$;
reset role;

-- ---------- admin triage ----------
set role authenticated;
do $$ begin
  begin perform public.admin_list_reports(); raise exception 'players cannot triage';
  exception when insufficient_privilege then null; end;
end $$;
reset role;
set role service_role;
do $$ declare q jsonb; harassment jsonb; r jsonb; begin
  q := public.admin_list_reports('open', 50);
  assert jsonb_array_length(q) = 2, q::text;
  select e into harassment from jsonb_array_elements(q) e where e->>'reason' = 'harassment';
  assert (harassment->>'open_reports_against')::int = 2, 'Bravo has two open reports';
  assert harassment->>'reporter_name' = 'Zoë Ångström';
  r := public.admin_resolve_report('mod@studio', (harassment->>'id')::bigint, 'actioned', 'Chat abuse',
                                   p_reset_name => true, p_delete_message => true);
  assert r->>'status' = 'actioned' and r->>'resolved_by' = 'mod@studio', r::text;
  begin perform public.admin_resolve_report('mod@studio', (harassment->>'id')::bigint, 'dismissed', 'x'); raise exception 'twice';
  exception when others then assert sqlerrm = 'report_already_resolved', sqlerrm; end;
  begin perform public.admin_resolve_report('mod@studio', 999999, 'dismissed', 'x'); raise exception 'missing';
  exception when others then assert sqlerrm = 'report_not_found', sqlerrm; end;
  begin perform public.admin_resolve_report('mod@studio', 1, 'open', 'x'); raise exception 'status';
  exception when others then assert sqlerrm = 'invalid_status', sqlerrm; end;
end $$;
reset role;
do $$ begin
  assert (select display_name from public.profiles where id = '22222222-2222-2222-2222-222222222222') ~ '^Voyager \d{4}$',
    'offensive name reset';
  assert not exists (select 1 from public.guild_messages where body = 'you are all terrible'), 'message deleted';
  assert (select message_snapshot from public.player_reports where reason = 'harassment') = 'you are all terrible',
    'evidence kept after deletion';
  assert (select count(*) from public.admin_actions where action = 'resolve_report') = 1;
end $$;
\echo PASS 05_invites_reports

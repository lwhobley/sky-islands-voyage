\set ON_ERROR_STOP 1
-- Guilds. Runs after 01/02. Alpha=1111, Bravo=2222, Charlie=3333.

-- Alpha founds a guild
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare g jsonb; begin
  assert public.get_my_guild() is null;
  g := public.create_guild('Brass Corsairs', 'brc', 'We raid at dawn');
  assert g->>'tag' = 'BRC' and g->>'my_role' = 'leader' and jsonb_array_length(g->'members') = 1, g::text;
  assert g->'messages'->0->>'body' like '%founded the guild.', g::text;
  begin perform public.create_guild('Second', 'SEC'); raise exception 'one guild per player';
  exception when others then assert sqlerrm = 'already_in_guild', sqlerrm; end;
  begin perform public.send_guild_message('   '); raise exception 'blank';
  exception when others then assert sqlerrm = 'invalid_message', sqlerrm; end;
  perform public.send_guild_message('Welcome aboard!');
end $$;
reset role;

-- Bravo: name clash, browse, join, chat visibility
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare l jsonb; g jsonb; gid uuid; begin
  begin perform public.create_guild('brass corsairs', 'XYZ'); raise exception 'case-insensitive name clash';
  exception when others then assert sqlerrm = 'guild_name_taken', sqlerrm; end;
  begin perform public.create_guild('ok name', 'bad-tag!'); raise exception 'bad tag';
  exception when others then assert sqlerrm = 'invalid_guild_details', sqlerrm; end;
  assert (select count(*) from public.guild_messages) = 0, 'non-members cannot read chat';
  l := public.list_guilds('corsair');
  assert jsonb_array_length(l) = 1 and (l->0->>'members')::int = 1, l::text;
  assert jsonb_array_length(public.list_guilds('BRC')) = 1, 'search by tag';
  gid := (l->0->>'id')::uuid;
  g := public.join_guild(gid);
  assert g->>'my_role' = 'member' and jsonb_array_length(g->'members') = 2, g::text;
  assert (select count(*) from public.guild_messages) = 3, 'member reads history (founded, welcome, joined)';
  for k in 1..5 loop perform public.send_guild_message('spam ' || k); end loop;
  begin perform public.send_guild_message('one too many'); raise exception 'rate limit';
  exception when others then assert sqlerrm = 'rate_limited', sqlerrm; end;
  begin perform public.kick_guild_member('11111111-1111-1111-1111-111111111111'); raise exception 'member kick';
  exception when others then assert sqlerrm = 'insufficient_guild_role', sqlerrm; end;
end $$;
reset role;

-- Charlie joins; Alpha promotes Bravo to officer; Bravo kicks Charlie
set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ begin perform public.join_guild((select id from public.guilds limit 1)); end $$;
reset role;
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ declare g jsonb; begin
  g := public.set_guild_role('22222222-2222-2222-2222-222222222222', 'officer');
  assert g->'members'->1->>'role' = 'officer', g::text;
end $$;
reset role;
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ declare g jsonb; begin
  g := public.kick_guild_member('33333333-3333-3333-3333-333333333333');
  assert jsonb_array_length(g->'members') = 2, g::text;
  begin perform public.kick_guild_member('11111111-1111-1111-1111-111111111111'); raise exception 'officer kicks leader';
  exception when others then assert sqlerrm = 'insufficient_guild_role', sqlerrm; end;
end $$;
reset role;
-- kicked player loses chat access
set request.jwt.claim.sub = '33333333-3333-3333-3333-333333333333';
set role authenticated;
do $$ begin
  assert (select count(*) from public.guild_messages) = 0, 'kicked player sees no chat';
  begin perform public.send_guild_message('hello?'); raise exception 'not member';
  exception when others then assert sqlerrm = 'not_in_guild', sqlerrm; end;
end $$;
reset role;

-- leader leaves -> officer Bravo inherits; Bravo leaves -> guild disbanded
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ begin perform public.leave_guild(); assert public.get_my_guild() is null; end $$;
reset role;
do $$ begin
  assert (select role from public.guild_members where user_id = '22222222-2222-2222-2222-222222222222') = 'leader', 'succession';
end $$;
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ begin perform public.leave_guild(); end $$;
reset role;
do $$ begin
  assert (select count(*) from public.guilds) = 0 and (select count(*) from public.guild_messages) = 0, 'disbanded';
end $$;

-- 30-member cap
insert into auth.users (id) select gen_random_uuid() from generate_series(1, 30);
set request.jwt.claim.sub = '11111111-1111-1111-1111-111111111111';
set role authenticated;
do $$ begin perform public.create_guild('Full House', 'FULL'); end $$;
reset role;
insert into public.guild_members (user_id, guild_id, role)
select p.id, (select id from public.guilds), 'member'
  from public.profiles p
 where p.id not in ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222',
                    '33333333-3333-3333-3333-333333333333')
 limit 29;
set request.jwt.claim.sub = '22222222-2222-2222-2222-222222222222';
set role authenticated;
do $$ begin
  assert jsonb_array_length(public.list_guilds(null)) = 0, 'full guilds are hidden';
  begin perform public.join_guild((select id from public.guilds)); raise exception 'cap';
  exception when others then assert sqlerrm = 'guild_full', sqlerrm; end;
end $$;
reset role;
\echo PASS 03_guilds

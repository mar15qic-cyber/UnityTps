"""Bounded orchestration for the explicitly opt-in RuntimeAuditDriver builds.

Fixture placement/equipment is evidence, not a gameplay result. All measured
shots and actions go through the existing input, prediction, RPC and damage path.
"""
import argparse
import csv
import json
import math
import statistics
import time
from pathlib import Path

ROLES = ('server', 'alpha', 'bravo', 'charlie')


def read(path):
    for _ in range(20):
        try:
            return json.loads(path.read_text(encoding='utf-8-sig'))
        except (FileNotFoundError, PermissionError, json.JSONDecodeError):
            time.sleep(.05)
    raise RuntimeError('Cannot read ' + str(path))


def write(path, obj):
    temp = path.with_suffix('.control.tmp')
    temp.write_text(json.dumps(obj), encoding='utf-8')
    for _ in range(40):
        try:
            temp.replace(path)
            return
        except PermissionError:
            time.sleep(.025)
    raise RuntimeError('Cannot replace ' + str(path))


class Audit:
    def __init__(self, directory):
        self.d = Path(directory).resolve()
        self.case = 'baseline'
        self.prefix = ''
        deadline=time.monotonic()+60
        while not all((self.d/(r+'.state.json')).exists() for r in ROLES):
            if time.monotonic()>deadline: raise RuntimeError('Driver startup deadline')
            time.sleep(.5)
        self.seq = {r: read(self.d / (r + '.state.json'))['seq'] for r in ROLES}
        self.network_seq = read(self.d / 'network.json')['seq'] if (self.d / 'network.json').exists() else 0
        self.ids = {}
        for r in ROLES[1:]:
            owner = [p for p in self.state(r)['players'] if p['owner']]
            if len(owner) == 1:
                self.ids[r] = owner[0]['connection']

    def state(self, role):
        return read(self.d / (role + '.state.json'))

    def states(self):
        return {r: self.state(r) for r in ROLES}

    def cmd(self, roles, op, **kwargs):
        roles = (roles,) if isinstance(roles, str) else roles
        ack = kwargs.pop('_ack', True)
        for r in roles:
            self.seq[r] += 1
            write(self.d / (r + '.command.json'), dict(seq=self.seq[r], op=op, caseId=self.case, **kwargs))
        if op == 'quit' or not ack:
            return
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            if all(self.state(r)['seq'] >= self.seq[r] for r in roles):
                return
            time.sleep(.03)
        raise RuntimeError('Command acknowledgment deadline: ' + str(roles) + ' ' + op)

    def mark(self, case):
        self.case = self.prefix + case
        states = self.states()
        if any(states[r]['phase'] != 'InProgress' or len([p for p in states[r]['players'] if p['owner']]) != 1
               for r in ROLES[1:]):
            self.log('invalid-session', states=states)
            raise RuntimeError('Case cannot run without all three live owners: ' + self.case)
        self.case_counts = {k: states['alpha'][k] for k in ('predicted','accepted','rejected')}
        self.cmd(ROLES, 'stop')
        print('CASE ' + case, flush=True)

    def wait(self, seconds):
        time.sleep(seconds)

    def log(self, kind, **data):
        with (self.d / 'cases.jsonl').open('a', encoding='utf-8') as f:
            f.write(json.dumps(dict(kind=kind, caseId=self.case, utc=time.time(), **data)) + '\n')

    def net(self, delay=0, loss=0, jitter=0, sequence=None, peer='alpha'):
        self.network_seq += 1
        setting = dict(delay_ms=delay, loss_percent=loss, jitter_ms=jitter)
        if sequence:
            setting['jitter_sequence_ms'] = sequence
        config = dict(seq=self.network_seq, caseId=self.case, seed=927, delay_ms=0, loss_percent=0,
                      peers={str(27780 + ROLES[1:].index(peer)): setting})
        write(self.d / 'network.json', config)
        self.log('network', config=config)

    def fixture(self, pitch=-65, weapon='rifle.02'):
        if len(self.ids) != 3:
            raise RuntimeError('Need three authenticated owners: ' + str(self.ids))
        self.mark('fixture-open-platform')
        self.cmd(ROLES, 'geometry', name='floor', position=dict(x=200,y=8,z=200), scale=dict(x=45,y=1,z=70))
        positions = {'alpha': (200,8.55,185), 'bravo': (200,8.55,205), 'charlie': (210,8.55,205)}
        for role, xyz in positions.items():
            self.cmd(ROLES, 'pose', target=self.ids[role], position=dict(zip('xyz',xyz)), yaw=0, pitch=pitch if role=='alpha' else 0)
        self.equip(weapon)
        self.cmd(ROLES[1:], 'stop', fps=60)
        self.wait(3)
        self.log('fixture-ready', ids=self.ids, states=self.states())

    def equip(self, weapon, role='alpha', slots=None):
        # Server Equip emits an ammo SyncVar before the explicit RestoreAmmo fixture.
        # Let that old snapshot drain before placing the client fixture, otherwise it
        # can overwrite the virtual player's full magazine with the previous empty one.
        chosen_slots = slots or [weapon, 'rifle.02' if weapon == 'pistol.day2' else 'pistol.day2']
        self.cmd('server', 'equip', target=self.ids[role], weapon=weapon, slots=chosen_slots)
        self.wait(.8)
        self.cmd(ROLES[1:], 'equip', target=self.ids[role], weapon=weapon, slots=chosen_slots)
        self.wait(.3)

    def reset(self, role='alpha'):
        self.cmd(ROLES, 'reset', target=self.ids[role])

    def fire(self, duration=1.5, **kwargs):
        self.cmd('alpha', 'input', duration=duration, fire=True, **kwargs)
        self.wait(duration + .45)

    def finish(self):
        self.cmd(ROLES, 'stop')
        states = self.states()
        self.log('case-end', states=states, proxy=read(self.d/'proxy.state.json'))
        if self.case != 'match-ended-shot' and any(states[r]['phase'] != 'InProgress' or
                len([p for p in states[r]['players'] if p['owner']]) != 1 for r in ROLES[1:]):
            self.log('invalid-session', states=states)
            raise RuntimeError('Session ended during case: ' + self.case)
        start = getattr(self, 'case_counts', None)
        if start and self.case != 'match-ended-shot':
            predicted = states['alpha']['predicted'] - start['predicted']
            accepted = states['alpha']['accepted'] - start['accepted']
            rejected = states['alpha']['rejected'] - start['rejected']
            if predicted >= 2 and accepted == 0 and rejected >= predicted:
                self.log('all-shots-rejected', predicted=predicted, rejected=rejected)
                # Two isolated shots may both miss the time budget during a real
                # retransmission. Preserve them as failures, then measure the paired
                # continuous-fire case; a fully rejected burst still stops the suite.
                if not self.case.startswith('network-') or predicted >= 10:
                    raise RuntimeError('All measured shots rejected; investigate before continuing: ' + self.case)

    def fps(self, full=False, only=None):
        self.fixture()
        root = Path(__file__).resolve().parents[2]
        with (root/'Assets/_Project/ScriptableObjects/Weapons/Tuning/MainlineWeaponTuning.csv').open(encoding='utf-8-sig') as f:
            tuning = list(csv.DictReader(f))
        for row in tuning:
            weapon = row['WeaponId']
            fps_list = [15,30,60,120,144,240] if full or weapon in ('rifle.02','smg.03','pistol.day2','sniper.01') else [60]
            for fps in fps_list:
                case='fps-' + weapon + '-' + str(fps)
                if only is not None and case not in only: continue
                self.mark(case)
                self.equip(weapon)
                self.cmd('alpha','stop',fps=fps)
                self.wait(1.1)
                check=self.state('alpha')
                own=next(p for p in check['players'] if p['owner'])
                if check['phase']!='InProgress' or own['ammo']<2 or own['weapon']!=weapon:
                    self.log('invalid-fixture',states=self.states())
                    raise RuntimeError('FPS fixture not ready: '+case)
                seconds = 4.3 if weapon.startswith('sniper.') else 1.7
                self.log('case-start', nominalRpm=float(row['Stat.Rpm']), requestedFps=fps, states=self.states())
                self.fire(seconds, clickInterval=.001)
                self.finish()
        self.cmd(ROLES[1:], 'stop', fps=60)

    def actions(self):
        self.fixture()
        for delay in (0,50):
            self.net(delay=delay)
            self.wait(2)
            for action in ('fire-reload','reload-fire','reload-swap','swap-fire','rapid-swap-fire','last-reload','last-swap','ads-reload','ads-swap'):
                self.mark('action-' + action + '-d' + str(delay))
                self.equip('rifle.02')
                self.wait(.6)
                self.fire(.25)
                self.wait(.4)
                self.log('case-start', states=self.states())
                if action == 'fire-reload': self.cmd('alpha','input',fire=True,reload=True,duration=.08)
                elif action == 'reload-fire':
                    self.cmd('alpha','input',reload=True,duration=.08)
                    self.cmd('alpha','input',fire=True,duration=.4)
                elif action == 'reload-swap':
                    self.cmd('alpha','input',reload=True,duration=.08)
                    self.cmd('alpha','input',slot=1,duration=.08)
                elif action == 'swap-fire': self.cmd('alpha','input',slot=1,fire=True,duration=.4)
                elif action == 'rapid-swap-fire':
                    self.cmd('alpha','input',slot=1,duration=.08)
                    self.cmd('alpha','input',slot=0,duration=.08)
                    self.cmd('alpha','input',fire=True,duration=.4)
                elif action.startswith('last-'):
                    self.fire(3.5)
                    self.cmd('alpha','input',fire=True,reload=action=='last-reload',slot=1 if action=='last-swap' else -1,duration=.2)
                elif action.startswith('ads-'):
                    self.cmd('alpha','input',aim=True,duration=.6)
                    self.wait(.4)
                    self.cmd('alpha','input',aim=True,reload=action=='ads-reload',slot=1 if action=='ads-swap' else -1,duration=.2)
                self.wait(3.3)
                self.log('action-settled', states=self.states())
                self.fire(.3, clickInterval=.001)
                self.finish()
            self.mark('action-sniper-to-pistol-first-shot-d' + str(delay))
            self.equip('sniper.02')
            self.wait(.5)
            self.fire(.01)
            self.log('before-switch', states=self.states())
            self.cmd('alpha','input',slot=1,fire=True,clickInterval=.001,duration=1.4)
            self.wait(2)
            self.finish()
        self.net()

    def network(self):
        self.fixture(pitch=0,weapon='pistol.day2')
        configs = [('delay'+str(d),dict(delay=d)) for d in (0,10,25,50,75,100)]
        configs += [('loss'+str(l),dict(delay=25,loss=l)) for l in (1,3,5,10)]
        configs += [('jitter',dict(sequence=[10,40,20,60,15]))]
        for name, setting in configs:
            self.mark('network-' + name)
            self.net(**setting)
            self.wait(5)
            self.reset('bravo')
            self.equip('pistol.day2')
            self.cmd('alpha','input',aim=True,aimTarget=self.ids['bravo'],duration=1)
            self.wait(.8)
            self.log('case-start', requested=setting, states=self.states())
            self.fire(.08, aim=True,aimTarget=self.ids['bravo'],aimHeight=1.2)
            self.wait(1)
            self.cmd('bravo','input',move=dict(x=1,y=0),duration=.7)
            self.fire(.08, aim=True,aimTarget=self.ids['bravo'])
            self.wait(1)
            self.cmd('alpha','input',move=dict(x=1,y=0),reload=True,duration=.7)
            self.wait(2)
            self.cmd('alpha','input',slot=1,duration=.08)
            self.wait(1)
            self.finish()
            self.mark('network-' + name + '-world-auto')
            pos=next(p['position'] for p in self.state('server')['players'] if p['connection']==self.ids['alpha'])
            self.cmd(ROLES,'pose',target=self.ids['alpha'],position=pos,yaw=0,pitch=-65)
            self.equip('smg.03')
            self.wait(1.5)
            self.log('case-start',requested=setting,states=self.states())
            self.fire(1.7)
            self.finish()
        self.net()

    def movement(self):
        self.fixture(pitch=0)
        for fps in (15,30,60,120,144,240):
            for action in ('walk','sprint','jump','ads-walk','lean-left','lean-right'):
                self.mark('movement-' + action + '-' + str(fps))
                self.cmd('alpha','stop',fps=fps)
                self.wait(.7)
                self.log('case-start', states=self.states())
                args = dict(move=dict(x=0,y=1),duration=1)
                if action=='sprint': args['sprint']=True
                if action=='jump': args['jump']=True
                if action=='ads-walk': args['aim']=True
                if action.startswith('lean'):
                    args['move']=dict(x=0,y=0)
                    args['lean']=-1 if action.endswith('left') else 1
                    args['aim']=True
                self.cmd('alpha','input',**args)
                self.wait(1.5)
                self.finish()
            self.cmd(ROLES,'pose',target=self.ids['alpha'],position=dict(x=200,y=8.55,z=185),yaw=0,pitch=0)
            self.wait(1)
        self.cmd('alpha','stop',fps=60)

    def lifecycle(self):
        self.fixture(pitch=0,weapon='pistol.day2')
        self.mark('blast-open-own-controller')
        self.cmd('server','blast-probe',target=self.ids['bravo'],position=dict(x=200,y=9.4,z=201))
        self.finish()
        self.death_and_end()

    def ads(self):
        self.fixture()
        for delay in (0,25,50,100):
            for transition in (0,.05,.08,.12,.3):
                self.mark('ads-d'+str(delay)+'-t'+str(transition))
                self.net(delay=delay)
                self.equip('rifle.02')
                self.wait(1.5)
                self.log('case-start',requestedTransitionSeconds=transition,states=self.states())
                if transition:
                    self.cmd('alpha','input',aim=True,duration=1,_ack=False)
                    self.wait(transition)
                self.cmd('alpha','input',aim=True,fire=True,duration=.01,_ack=False)
                self.wait(1.3)
                self.finish()
        self.net()

    def server_fps(self):
        self.net()
        self.fixture()
        for fps in (15,30,60,120):
            self.mark('server-fps-'+str(fps))
            self.cmd('server','stop',fps=fps)
            self.equip('rifle.02')
            self.wait(2.2)
            self.log('case-start',states=self.states())
            self.fire(1.7)
            self.cmd('alpha','input',move=dict(x=0,y=1),jump=True,fire=True,duration=1.2)
            self.wait(2)
            self.finish()
        self.cmd('server','stop',fps=45)

    def visual(self):
        self.fixture(pitch=0,weapon='rifle.02')
        for label,x,width,lean in [('solid-wall',200,2,0),('left-cover-right-peek',199.5,1,1),('right-cover-left-peek',200.5,1,-1),('left-cover-wrong-peek',199.5,1,-1),('right-cover-wrong-peek',200.5,1,1)]:
            self.mark('cover-'+label)
            self.cmd(ROLES,'geometry',name='cover',position=dict(x=x,y=10,z=186),scale=dict(x=width,y=3,z=.3))
            self.equip('rifle.02')
            deadline=time.monotonic()+8
            while True:
                target=next(p for p in self.state('server')['players'] if p['connection']==self.ids['bravo'])
                if not target['dead'] and not target['protectedNow']: break
                if time.monotonic()>deadline: raise RuntimeError('Cover target not ready: '+label)
                self.wait(.2)
            self.cmd(ROLES,'pose',target=self.ids['bravo'],position=dict(x=200,y=8.55,z=205),yaw=0,pitch=0)
            self.reset('bravo')
            self.cmd('alpha','input',aim=True,lean=lean,aimTarget=self.ids['bravo'],duration=1)
            self.wait(.7)
            self.log('case-start',states=self.states())
            self.cmd('alpha','input',aim=True,lean=lean,fire=True,aimTarget=self.ids['bravo'],duration=.8)
            self.cmd('alpha','screenshot',name=label)
            self.cmd('charlie','screenshot',name=label+'-observer')
            self.wait(1.5)
            self.finish()
        self.cmd(ROLES,'geometry',name='cover',scale=dict(x=0,y=0,z=0))
        self.mark('stairs-walk-ads-jump')
        for step in range(1,7):
            height=step*.18
            self.cmd(ROLES,'geometry',name='step'+str(step),position=dict(x=200,y=8.5+height/2,z=187+step*.45),scale=dict(x=4,y=height,z=.46))
        self.cmd('alpha','input',move=dict(x=0,y=1),aim=True,duration=1.6)
        self.wait(1.7)
        self.cmd('alpha','input',move=dict(x=0,y=1),jump=True,aim=True,fire=True,duration=.5)
        self.cmd('alpha','screenshot',name='stairs-ads-jump')
        self.wait(1.5)
        self.finish()

    def death_and_end(self):
        self.mark('death-colliders-respawn')
        self.cmd('server','kill-fixture',target=self.ids['bravo'])
        self.cmd('bravo','input',fire=True,reload=True,slot=1,duration=1)
        self.wait(1)
        self.log('dead-state',states=self.states())
        self.wait(5)
        self.finish()
        self.mark('match-ended-shot')
        self.cmd('alpha','input',aim=True,aimTarget=self.ids['charlie'],duration=1)
        self.wait(.8)
        self.net(delay=50)
        self.wait(.12)
        self.log('before-pending-shot',states=self.states())
        self.cmd('alpha','input',fire=True,aim=True,aimTarget=self.ids['charlie'],duration=.08,_ack=False)
        self.wait(.04)
        self.cmd('server','end-match',_ack=False)
        self.wait(.4)
        self.log('ended-with-inflight-shot',states=self.states())
        self.fire(.5,aim=True,aimTarget=self.ids['charlie'],clickInterval=.2)
        self.wait(1)
        self.finish()

    def smoke(self):
        self.mark('natural-join-baseline')
        self.log('joined', states=self.states(), ids=self.ids)
        self.cmd('alpha','screenshot',name='natural-join')
        self.fixture()
        self.mark('fire-smoke')
        self.fire(1)
        self.finish()

    def prepare(self):
        deadline = time.monotonic()+120
        next_retry = time.monotonic()+3
        while time.monotonic()<deadline:
            states=self.states()
            compact={r:{k:s.get(k) for k in ('scene','phase','uiPage','uiStatus','apiReady','roomPending','mapBusy')} for r,s in states.items()}
            print(json.dumps(compact,ensure_ascii=False),flush=True)
            if all(len([p for p in states[r]['players'] if p['owner']])==1 and states[r]['phase']=='InProgress' for r in ROLES[1:]):
                self.ids={r:next(p['connection'] for p in states[r]['players'] if p['owner']) for r in ROLES[1:]}
                self.cmd(ROLES[1:],'resolution')
                self.log('join-ready',states=self.states(),ids=self.ids)
                print('JOIN_READY '+json.dumps(self.ids),flush=True)
                return
            alpha=states['alpha']
            if time.monotonic()>=next_retry and not (self.d/'room-code.txt').exists() and alpha.get('apiReady') and not alpha.get('roomPending') and not alpha.get('mapBusy'):
                self.cmd('alpha','lobby-create')
                next_retry=time.monotonic()+5
            time.sleep(3)
        self.log('join-failed',states=self.states())
        raise RuntimeError('Normal lobby entry deadline; see state and game logs')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('directory',type=Path)
    parser.add_argument('suite',choices=['status','prepare','smoke','fps','fps-full','fps-recheck','actions','network','movement','lifecycle','ads','visual','server-fps','stop'])
    parser.add_argument('--prefix',default='')
    args = parser.parse_args()
    audit = Audit(args.directory)
    audit.prefix=args.prefix
    if args.suite=='status':
        print(json.dumps(dict(ids=audit.ids,states=audit.states()),ensure_ascii=False))
    elif args.suite=='stop':
        audit.cmd(ROLES,'quit')
        (audit.d/'proxy.stop').touch()
    elif args.suite=='fps-full': audit.fps(full=True)
    elif args.suite=='fps-recheck':
        prior=read(Path(__file__).resolve().parents[2]/'Logs/SystemAudit0927/run05/analysis.json')
        audit.fps(full=True,only={c['caseId'] for c in prior['cases'] if c['caseId'].startswith('fps-') and c['predicted']<2})
    else: getattr(audit,args.suite.replace('-','_'))()


if __name__=='__main__': main()

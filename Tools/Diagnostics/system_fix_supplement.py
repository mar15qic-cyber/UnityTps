"""Additional bounded virtual-player cases for the September 27 fixes."""
import argparse
from system_audit import Audit, ROLES

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('directory')
p.add_argument('suite', choices=['corpse', 'burst-jump', 'concurrent'])
args = p.parse_args()
a = Audit(args.directory)
a.net()

if args.suite == 'corpse':
    a.fixture(pitch=0, weapon='pistol.day2')
    a.mark('corpse-front-live-target-behind')
    a.cmd(ROLES, 'pose', target=a.ids['charlie'], position=dict(x=200, y=8.55, z=208), yaw=0, pitch=0)
    a.wait(1)
    a.cmd('alpha','input',aim=True,aimTarget=a.ids['charlie'],duration=1)
    a.wait(1)
    a.cmd('server','kill-fixture',target=a.ids['bravo'])
    a.wait(.35)
    a.log('corpse-before-fire', states=a.states())
    a.fire(.4, aim=True, aimTarget=a.ids['charlie'], clickInterval=.2)
    a.log('corpse-after-fire', states=a.states())
    a.wait(5)
    a.finish()
elif args.suite == 'burst-jump':
    a.fixture(pitch=-65, weapon='rifle.02')
    for fps in (15,60,144):
        a.mark('burst-jump-outage-'+str(fps))
        a.cmd('alpha','stop',fps=fps)
        a.cmd(ROLES,'pose',target=a.ids['alpha'],position=dict(x=200,y=8.55,z=185),yaw=0,pitch=-65)
        a.wait(1)
        a.log('case-start',states=a.states())
        a.cmd('alpha','input',jump=True,move=dict(x=0,y=1),fire=True,duration=1.2)
        a.wait(.15)
        a.net(loss=100)
        a.wait(.3)
        a.net(delay=25)
        a.wait(2.8)
        a.finish()
    a.net()
else:
    a.fixture()
    a.mark('concurrent-three-player-auto')
    for role, weapon in zip(ROLES[1:], ('smg.03','rifle.02','shotgun.01')):
        a.equip(weapon, role)
        own=next(x for x in a.state(role)['players'] if x['owner'])
        a.cmd(ROLES,'pose',target=a.ids[role],position=own['position'],yaw=0,pitch=-65)
    a.wait(1)
    a.log('case-start',states=a.states())
    a.cmd(ROLES[1:],'input',fire=True,duration=4,clickInterval=.001)
    a.wait(5)
    a.finish()

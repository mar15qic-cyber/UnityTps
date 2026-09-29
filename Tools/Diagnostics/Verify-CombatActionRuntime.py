"""Exercise the original duplicate-slot failure and rejected-action recovery on real RPCs."""
import sys
from system_audit import Audit, ROLES

a = Audit(sys.argv[1])
a.net()
a.fixture()
a.mark('duplicate-slot-switch-then-fire')
a.equip('pistol.day2', slots=['pistol.day2', 'pistol.day2'])
a.fire(.3, clickInterval=.001)
a.cmd('alpha', 'input', slot=1, duration=.08)
a.wait(1.5)
a.log('action-settled', states=a.states())
a.fire(.5, clickInterval=.001)
a.finish()

a.mark('duplicate-slot-followup-smg')
a.equip('smg.03')
a.fire(1)
a.finish()

a.mark('rejected-slot-switch-then-fire')
a.equip('pistol.day2')
# Deliberately omit slot 1 on authority only. This is an explicit invalid request
# fixture; the client must recover through the ordinary reliable action result.
a.cmd('server', 'equip', target=a.ids['alpha'], weapon='pistol.day2', slots=['pistol.day2'])
a.net(delay=50)
a.wait(1)
a.log('rejection-fixture', states=a.states())
a.cmd('alpha', 'input', slot=1, duration=.08)
a.wait(2)
a.log('action-settled', states=a.states())
client=next(p for p in a.state('alpha')['players'] if p['owner'])
server=next(p for p in a.state('server')['players'] if p['connection']==a.ids['alpha'])
assert client['submittedEquipment']==server['executedEquipment'], (client,server)
assert client['weapon']==server['weapon']=='pistol.day2', (client,server)
a.fire(.5, clickInterval=.001)
a.finish()
a.net()
print('ACTION_RECOVERY_VERIFIED', flush=True)

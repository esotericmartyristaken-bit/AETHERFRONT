"""Apply reviewed RC33 profile changes without repacking immutable assets."""
import copy

PLUME_ROOTS = {
    'RealPlume', 'RealPlume-Stock', 'SmokeScreen', 'StockWaterfallEffects',
    'Waterfall', 'WaterfallExtensions', 'WaterfallRestock',
}
REMOVED_ROOTS = {'PAPIPlugin', 'ZZZ_KESASPotato'}


def remove_declarations(profile):
    profile['managedPackages'] = [p for p in profile['managedPackages']
                                  if p['stripPrefix'] not in REMOVED_ROOTS]
    # An obsolete optional declaration would otherwise allow the removed mod
    # to remain installed. Unknown-mod consent remains the normal apply gate.
    profile['clientOnly'] = [p for p in profile['clientOnly']
                           if not any(root.replace('\\', '/').split('/')[1:2]
                                      and root.replace('\\', '/').split('/')[1] in REMOVED_ROOTS
                                      for root in p.get('pathRoots', []))]


def update_catalog(original, marker_package, marker_expanded_bytes):
    catalog = copy.deepcopy(original)
    for name in ('full', 'potato'):
        remove_declarations(catalog[name])
    # Potato keeps reduced scenery/clouds. Exhaust is operational feedback:
    # use the same existing, cached, complete plume packages as Regular.
    full = {p['stripPrefix']: p for p in catalog['full']['managedPackages']}
    assert PLUME_ROOTS <= full.keys(), 'Incomplete Regular plume dependencies'
    potato = catalog['potato']
    potato['managedPackages'] = [p for p in potato['managedPackages']
                                 if p['stripPrefix'] not in PLUME_ROOTS]
    potato['managedPackages'].extend(copy.deepcopy(full[r]) for r in sorted(PLUME_ROOTS))
    assert marker_package['stripPrefix'] == 'ZZZ_KESASPotato'
    potato['managedPackages'].append(copy.deepcopy(marker_package))
    catalog['sizes'].append({'id': marker_package['id'], 'expandedBytes': marker_expanded_bytes})
    for name in ('full', 'potato'):
        packages = catalog[name]['managedPackages']
        assert len({p['id'] for p in packages}) == len(packages)
        assert len({p['stripPrefix'].casefold() for p in packages}) == len(packages)
        assert 'PAPIPlugin' not in {p['stripPrefix'] for p in packages}
        assert ('ZZZ_KESASPotato' in {p['stripPrefix'] for p in packages}) == (name == 'potato')
    return catalog

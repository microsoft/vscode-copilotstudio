export interface ActivationMarker {
    exists: boolean;
    firstActivation?: string;
}

export interface ActivationDecision {
    standDown: boolean;
    message?: string;
}

export const activationDecision = (marker: ActivationMarker): ActivationDecision => {
    if (!marker.exists) {
        return { standDown: false };
    }

    const when = (marker.firstActivation ?? '').trim();

    return {
        standDown: true,
        message: [
            'this window is a second activation of the driver;',
            when ? `the run started at ${when}.` : 'the first activation left no timestamp.',
            'Standing down without running, reporting or quitting:',
            'a second pass would place fixtures over seeds that already carry .mcs,',
            'and quitting here would kill the activation that owns the run.',
        ].join(' '),
    };
};
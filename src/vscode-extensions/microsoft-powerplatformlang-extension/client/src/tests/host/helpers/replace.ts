let restorations: (() => void)[] = [];

export const replace = (target: Record<string, unknown>, key: string, value: unknown) => {
    const original = target[key];
    target[key] = value;
    restorations.push(() => { target[key] = original; });
};

export const restoreReplaced = () => {
    restorations.reverse().forEach(restore => restore());
    restorations = [];
};

export interface DiffChange {
  value: string;
  added?: true;
  removed?: true;
}

export class FileDiff {
  private localLines: string[];
  private remoteLines: string[];
  private localLength: number;
  private remoteLength: number;
  private traceList: Map<number, number>[] = [];

  constructor(local: string, remote: string) {
    this.localLines = local.split(/\r?\n/);
    this.remoteLines = remote.split(/\r?\n/);
    this.localLength = this.localLines.length;
    this.remoteLength = this.remoteLines.length;
  }

  // Computes the diff between the local and remote text by using Myers' algorithm.
  public computeDiff(): DiffChange[] {
    let furthestXMap = new Map<number, number>();
    furthestXMap.set(1, 0);

    for (let editDistance = 0; editDistance <= this.localLength + this.remoteLength; editDistance++) {
      const currentStepMap = new Map<number, number>();

      for (let diagonal = -editDistance; diagonal <= editDistance; diagonal += 2) {
        const valueFromKMinus1 = furthestXMap.get(diagonal - 1);
        const valueFromKPlus1 = furthestXMap.get(diagonal + 1);

        let currentX: number;
        if (diagonal === -editDistance || (diagonal !== editDistance && (valueFromKMinus1 ?? -1) < (valueFromKPlus1 ?? -1))) {
          currentX = valueFromKPlus1 ?? 0;
        } else {
          currentX = (valueFromKMinus1 ?? 0) + 1;
        }

        let currentY = currentX - diagonal;

        while (currentX < this.localLength && currentY < this.remoteLength && this.localLines[currentX] === this.remoteLines[currentY]) {
          currentX++;
          currentY++;
        }

        currentStepMap.set(diagonal, currentX);

        if (currentX >= this.localLength && currentY >= this.remoteLength) {
          this.traceList.push(currentStepMap);
          return this.buildDiff();
        }
      }

      this.traceList.push(currentStepMap);
      furthestXMap = currentStepMap;
    }

    return this.buildDiff();
  }

  private buildDiff(): DiffChange[] {
    const result: DiffChange[] = [];
    let x = this.localLength;
    let y = this.remoteLength;

    for (let editDistance = this.traceList.length - 1; editDistance >= 0; editDistance--) {
      const diagonal = x - y;
      const previousMap = this.traceList[editDistance - 1];

      if (editDistance === 0) {
        while (x > 0 && y > 0) {
          x--;
          y--;
          result.unshift({ value: this.localLines[x] + '\n' });
        }
        for (let i = 0; i < x; i++) {
          result.unshift({ value: this.localLines[i] + '\n', removed: true });
        }
        for (let i = 0; i < y; i++) {
          result.unshift({ value: this.remoteLines[i] + '\n', added: true });
        }
        break;
      }

      const valueFromKMinus1 = previousMap?.get(diagonal - 1);
      const valueFromKPlus1 = previousMap?.get(diagonal + 1);

      if (valueFromKMinus1 === undefined && valueFromKPlus1 === undefined) {
        return [];
      }

      let previousDiagonal: number;
      if (diagonal === -editDistance || (valueFromKMinus1 ?? -1) < (valueFromKPlus1 ?? -1)) {
        previousDiagonal = diagonal + 1;
      } else {
        previousDiagonal = diagonal - 1;
      }

      const previousX = previousMap?.get(previousDiagonal) ?? 0;
      const previousY = previousX - previousDiagonal;

      while (x > previousX && y > previousY) {
        x--;
        y--;
        result.unshift({ value: this.localLines[x] + '\n' });
      }

      if (x === previousX && y > previousY) {
        for (let i = previousY; i < y; i++) {
          result.unshift({ value: this.remoteLines[i] + '\n', added: true });
        }
      } else if (y === previousY && x > previousX) {
        for (let i = previousX; i < x; i++) {
          result.unshift({ value: this.localLines[i] + '\n', removed: true });
        }
      }

      x = previousX;
      y = previousY;
    }

    return result;
  }
}

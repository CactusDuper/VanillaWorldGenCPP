# VanillaWorldGenCPP

Full 1:1 bit for bit accurate Terraira WorldGen rewrite in C++. ~3.3x faster than TML, ~5.5x faster than Vanilla.

## Benchmark Results
All benchmarks were run on the same set of 50 random seeds. Benchmarked against 1.4.4.9.

### Summary

**Total Time comparision**

<p align="center">
  <img src="assets/total_time_comparison.png" alt="Total Time graph" width="80%">
</p>

This project is, on average:
- **~5.5x faster** than vanilla Terraria.
- **~3.3x faster** than tModLoader.

---

<details>
<summary><strong>Click here for Detailed Performance Breakdowns by world size</strong></summary>

<br>

<details>
<summary><strong>Small Worlds</strong></summary>

#### Performance Factor
![Small factor](assets/performance_factor_small.png)

#### Per-Pass Absolute Timings
![Small absolute](assets/per_pass_absolute_small.png)

#### Top 20 Best Performers
![Small best](assets/top_20_best_performers_small.png)

</details>

<details>
<summary><strong>Medium Worlds</strong></summary>

#### Performance Factor
![Medium factor](assets/performance_factor_medium.png)

#### Per-Pass Absolute Timings
![Medium absolute](assets/per_pass_absolute_medium.png)

#### Top 20 Best Performers
![Medium best](assets/top_20_best_performers_medium.png)

</details>

<details>
<summary><strong>Large Worlds</strong></summary>

#### Performance Factor
![Large factor](assets/performance_factor_large.png)

#### Per-Pass Absolute Timings
![Large absolute](assets/per_pass_absolute_large.png)

#### Top 20 Best Performers
![Large best](assets/top_20_best_performers_large.png)

</details>

</details>

## Why?
Haha cpu go BRRRRRRRRRRRRRRRRR. I just wanted to make Terraria Worldgen fast. Fast enough to generate every seed and find optimal seeds for speedrunners. A standalone multithreaded version exists, it is not yet public.

## How?
Hundreds of hours cleaning and optimizing. Better algorithms. Better data structures. Better memory layout and cache optimization. SIMD, vectorization, using a good compiler (thank you Clang!).

Optimizing while maintaining 1:1 vanilla compatibility is hard. Mathematically equivalent code does not matter when floating point precision errors exist. Every random call must be done in the same order.
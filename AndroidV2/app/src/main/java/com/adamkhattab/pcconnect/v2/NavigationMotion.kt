package com.adamkhattab.pcconnect.v2

import androidx.compose.animation.AnimatedContentTransitionScope
import androidx.compose.animation.EnterTransition
import androidx.compose.animation.ExitTransition
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.scaleIn
import androidx.compose.animation.scaleOut
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.tween
import androidx.navigation.NavBackStackEntry

private const val SharedAxisDurationMillis = 280
private const val FadeThroughDurationMillis = 180

internal fun fadeThroughEnter(): EnterTransition =
    fadeIn(tween(FadeThroughDurationMillis, delayMillis = 45, easing = FastOutSlowInEasing)) +
        scaleIn(
            animationSpec = tween(FadeThroughDurationMillis, delayMillis = 45, easing = FastOutSlowInEasing),
            initialScale = 0.98f,
        )

internal fun fadeThroughExit(): ExitTransition =
    fadeOut(tween(90, easing = FastOutSlowInEasing)) +
        scaleOut(tween(90, easing = FastOutSlowInEasing), targetScale = 0.98f)

internal fun AnimatedContentTransitionScope<NavBackStackEntry>.sharedAxisForwardEnter(): EnterTransition =
    slideIntoContainer(
        towards = AnimatedContentTransitionScope.SlideDirection.Left,
        animationSpec = tween(SharedAxisDurationMillis, easing = FastOutSlowInEasing),
        initialOffset = { fullDistance -> fullDistance / 4 },
    ) + fadeIn(tween(SharedAxisDurationMillis, delayMillis = 35, easing = FastOutSlowInEasing))

internal fun AnimatedContentTransitionScope<NavBackStackEntry>.sharedAxisForwardExit(): ExitTransition =
    slideOutOfContainer(
        towards = AnimatedContentTransitionScope.SlideDirection.Left,
        animationSpec = tween(SharedAxisDurationMillis, easing = FastOutSlowInEasing),
        targetOffset = { fullDistance -> fullDistance / 4 },
    ) + fadeOut(tween(140, easing = FastOutSlowInEasing))

internal fun AnimatedContentTransitionScope<NavBackStackEntry>.sharedAxisBackwardEnter(): EnterTransition =
    slideIntoContainer(
        towards = AnimatedContentTransitionScope.SlideDirection.Right,
        animationSpec = tween(SharedAxisDurationMillis, easing = FastOutSlowInEasing),
        initialOffset = { fullDistance -> fullDistance / 4 },
    ) + fadeIn(tween(SharedAxisDurationMillis, delayMillis = 35, easing = FastOutSlowInEasing))

internal fun AnimatedContentTransitionScope<NavBackStackEntry>.sharedAxisBackwardExit(): ExitTransition =
    slideOutOfContainer(
        towards = AnimatedContentTransitionScope.SlideDirection.Right,
        animationSpec = tween(SharedAxisDurationMillis, easing = FastOutSlowInEasing),
        targetOffset = { fullDistance -> fullDistance / 4 },
    ) + fadeOut(tween(140, easing = FastOutSlowInEasing))
